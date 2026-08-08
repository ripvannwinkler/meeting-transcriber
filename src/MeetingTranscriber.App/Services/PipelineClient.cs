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

    public async Task<string> SummarizeAsync(
        string transcript,
        IProgress<string>? progress = null,
        CancellationToken ct = default
    )
    {
        var args = $"\"{CliScript}\" summarize --config \"{Paths.SettingsFile}\"";
        return await RunBackendAsync(args, progress, ct, stdinText: transcript);
    }

    /// <summary>
    /// Runs the backend, parses its NDJSON, and returns the "transcript"/"summary"
    /// payload. Reports "progress" events and raises on an "error" event or non-zero exit.
    /// </summary>
    private static async Task<string> RunBackendAsync(
        string arguments,
        IProgress<string>? progress,
        CancellationToken ct,
        string? stdinText = null
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
            RedirectStandardInput = stdinText != null,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process =
            Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start backend process.");

        // Feed stdin text (if any) and drain stderr on background tasks so the pipes
        // never block the backend while it runs (tqdm bars and torch noise land on
        // stderr and are ignored).
        var stderrTask = process.StandardError.ReadToEndAsync();
        Task? stdinTask = null;
        if (stdinText != null)
        {
            stdinTask = process
                .StandardInput.WriteAsync(stdinText)
                .ContinueWith(_ => process.StandardInput.Close(), CancellationToken.None);
        }

        string? result = null;
        string? backendError = null;

        while (true)
        {
            var line = await process.StandardOutput.ReadLineAsync(ct);
            if (line is null)
                break;
            HandleLine(line, progress, ref result, ref backendError);
        }

        await process.WaitForExitAsync(ct);
        if (stdinTask != null)
            await stdinTask;
        var stderr = await stderrTask;

        if (result is not null)
            return result;

        if (backendError is not null)
            throw new InvalidOperationException(backendError);

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"Backend failed (exit {process.ExitCode}): {Tail(stderr)}"
            );

        throw new InvalidOperationException("Backend ended without producing a result.");
    }

    private static void HandleLine(
        string line,
        IProgress<string>? progress,
        ref string? result,
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
                case "summary":
                    if (doc.RootElement.TryGetProperty("text", out var tt))
                        result = tt.GetString() ?? "";
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
