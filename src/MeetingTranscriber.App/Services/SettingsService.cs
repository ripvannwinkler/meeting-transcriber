using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
        // snake_case matches the Python backend (config.py) and settings.example.json.
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
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
            // Tolerate legacy/manual files with camelCase keys (e.g. "baseUrl",
            // "maxTokens") by normalizing every key to snake_case before binding.
            var node = JsonNode.Parse(File.ReadAllText(SettingsPath));
            NormalizeKeysToSnakeCase(node);
            var json = node?.ToJsonString();
            var loaded = string.IsNullOrEmpty(json)
                ? null
                : JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
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

    /// <summary>Rewrites every JSON object key to snake_case (camelCase keys become readable by both sides).</summary>
    private static void NormalizeKeysToSnakeCase(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            foreach (var pair in obj.ToList())
            {
                var snake = ToSnakeCase(pair.Key);
                if (snake != pair.Key)
                {
                    obj.Remove(pair.Key);
                    obj[snake] = pair.Value;
                }
                NormalizeKeysToSnakeCase(pair.Value);
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array)
                NormalizeKeysToSnakeCase(item);
        }
    }

    private static string ToSnakeCase(string name)
    {
        var sb = new StringBuilder(name.Length + 4);
        for (int i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]))
            {
                if (i > 0)
                    sb.Append('_');
                sb.Append(char.ToLowerInvariant(name[i]));
            }
            else
            {
                sb.Append(name[i]);
            }
        }
        return sb.ToString();
    }
}
