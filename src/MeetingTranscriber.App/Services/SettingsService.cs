using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MeetingTranscriber.App.Services;

/// <summary>Speech-to-text settings — mirrors the "stt" section of settings.json.</summary>
public sealed class SttSettings
{
    public string Engine { get; set; } = "whisper";
    public string Variant { get; set; } = "medium";
    public string CacheDir { get; set; } = "models/stt";
    public bool AutoDownload { get; set; } = true;
    public string Device { get; set; } = "cuda";
}

/// <summary>OpenAI-compatible API settings — mirrors the "api" section of settings.json.</summary>
public sealed class ApiSettings
{
    public string BaseUrl { get; set; } = "http://localhost:1234/v1";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "";
    public int MaxTokens { get; set; } = 4096;
}

/// <summary>Top-level app config, serialized to the single unified settings.json.</summary>
public sealed class AppSettings
{
    public SttSettings Stt { get; set; } = new();
    public ApiSettings Api { get; set; } = new();
    public string OutputDir { get; set; } = "output";
}

/// <summary>
/// Loads and saves the unified settings.json shared with the Python backend.
/// Missing keys fall back to defaults, mirroring backend/config.py semantics.
/// </summary>
public sealed class SettingsService
{
    public static readonly string[] ValidSttVariants =
    [
        "tiny",
        "base",
        "small",
        "medium",
        "large",
        "large-v3",
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string SettingsPath { get; } = Paths.SettingsFile;

    public AppSettings Load()
    {
        var settings = CloneDefaults();
        if (!File.Exists(SettingsPath))
            return settings;

        try
        {
            var loaded = JsonSerializer.Deserialize<AppSettings>(
                File.ReadAllText(SettingsPath),
                JsonOptions
            );
            if (loaded != null)
                Merge(settings, loaded);
        }
        catch (JsonException)
        {
            // Corrupt/incomplete settings: fall back to defaults rather than crash.
        }

        if (!ValidSttVariants.Contains(settings.Stt.Variant))
            settings.Stt.Variant = "medium";

        return settings;
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
    }

    private static AppSettings CloneDefaults() =>
        JsonSerializer.Deserialize<AppSettings>(
            JsonSerializer.Serialize(new AppSettings(), JsonOptions),
            JsonOptions
        )!;

    private static void Merge(AppSettings target, AppSettings loaded)
    {
        if (loaded.Stt != null)
        {
            target.Stt.Engine = NullIfEmpty(loaded.Stt.Engine) ?? target.Stt.Engine;
            target.Stt.Variant = NullIfEmpty(loaded.Stt.Variant) ?? target.Stt.Variant;
            target.Stt.CacheDir = NullIfEmpty(loaded.Stt.CacheDir) ?? target.Stt.CacheDir;
            target.Stt.AutoDownload = loaded.Stt.AutoDownload;
            target.Stt.Device = NullIfEmpty(loaded.Stt.Device) ?? target.Stt.Device;
        }

        if (loaded.Api != null)
        {
            target.Api.BaseUrl = NullIfEmpty(loaded.Api.BaseUrl) ?? target.Api.BaseUrl;
            target.Api.ApiKey = loaded.Api.ApiKey ?? "";
            target.Api.Model = loaded.Api.Model ?? "";
            target.Api.MaxTokens =
                loaded.Api.MaxTokens > 0 ? loaded.Api.MaxTokens : target.Api.MaxTokens;
        }

        if (!string.IsNullOrWhiteSpace(loaded.OutputDir))
            target.OutputDir = loaded.OutputDir;
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
