using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace MeetingTranscriber.App.Services;

/// <summary>
/// IConversationPipeline backed by the Python backend (backend/.venv). Spawns
/// <c>cli.py</c> as a subprocess, streams its newline-delimited JSON events, and
/// reports progress to the caller. Non-JSON / unknown stderr noise is ignored.
/// </summary>
public sealed class PipelineClient : IConversationPipeline
{
    private static readonly string PythonExe = Path.Combine(
        Paths.RepoRoot,
        "backend",
        ".venv",
        "Scripts",
        "python.exe"
    );

    private static readonly string CliScript = Path.Combine(Paths.RepoRoot, "backend", "cli.py");

    public async Task<string> TranscribeAsync(
        string wavPath,
        IProgress<string>? progress = null,
        CancellationToken ct = default
    )
    {
        var args = $"\"{CliScript}\" transcribe \"{wavPath}\" --config \"{Paths.SettingsFile}\"";
        return await RunBackendAsync(args, progress, ct);
    }

    public Task<string> SummarizeAsync(
        string transcript,
        IProgress<string>? progress = null,
        CancellationToken ct = default
    )
    {
        // Wired to the backend in Phase 4 (cli.py summarize + transcribe.py/summarize.py).
        return Task.FromResult("(Summarization is wired up in Phase 4.)");
    }

    /// <summary>
    /// Runs the backend, parses its NDJSON, and returns the "transcript" payload.
    /// Reports "progress" events and raises on an "error" event or non-zero exit.
    /// </summary>
    private static async Task<string> RunBackendAsync(
        string arguments,
        IProgress<string>? progress,
        CancellationToken ct
    )
    {
        if (!File.Exists(PythonExe))
            throw new FileNotFoundException(
                $"Python venv not found at {PythonExe}. Run scripts/setup_backend.ps1 first."
            );

        var psi = new ProcessStartInfo
        {
            FileName = PythonExe,
            Arguments = arguments,
            WorkingDirectory = Path.Combine(Paths.RepoRoot, "backend"),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process =
            Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start backend process.");

        // Drain stderr on a background task so the pipe never blocks the backend
        // (tqdm bars and torch noise land here and are ignored).
        var stderrTask = process.StandardError.ReadToEndAsync();

        string? transcript = null;
        string? backendError = null;

        while (true)
        {
            var line = await process.StandardOutput.ReadLineAsync(ct);
            if (line is null)
                break;
            HandleLine(line, progress, ref transcript, ref backendError);
        }

        await process.WaitForExitAsync(ct);
        var stderr = await stderrTask;

        if (transcript is not null)
            return transcript;

        if (backendError is not null)
            throw new InvalidOperationException(backendError);

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"Backend failed (exit {process.ExitCode}): {Tail(stderr)}"
            );

        throw new InvalidOperationException("Backend ended without producing a transcript.");
    }

    private static void HandleLine(
        string line,
        IProgress<string>? progress,
        ref string? transcript,
        ref string? backendError
    )
    {
        if (string.IsNullOrWhiteSpace(line))
            return;

        JsonDocument? doc;
        try
        {
            doc = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return; // stray/stderr-mixed noise — ignore
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("type", out var typeEl))
                return;
            var type = typeEl.GetString();

            switch (type)
            {
                case "progress":
                    if (progress != null && doc.RootElement.TryGetProperty("message", out var pm))
                        progress.Report(pm.GetString() ?? "");
                    break;
                case "transcript":
                    if (doc.RootElement.TryGetProperty("text", out var tt))
                        transcript = tt.GetString() ?? "";
                    break;
                case "error":
                    if (doc.RootElement.TryGetProperty("message", out var em))
                        backendError = em.GetString();
                    break;
            }
        }
    }

    private static string Tail(string text, int maxChars = 800)
    {
        if (string.IsNullOrEmpty(text))
            return "(no output)";
        return text.Length <= maxChars ? text : text[^maxChars..];
    }
}
