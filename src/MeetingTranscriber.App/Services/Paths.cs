using System.IO;

namespace MeetingTranscriber.App.Services;

/// <summary>Central paths, resolved relative to the repository root (found by walking up to the solution file).</summary>
public static class Paths
{
    private static readonly string Root = FindRepoRoot();

    public static string RepoRoot => Root;
    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string RecordingsDir => Path.Combine(Root, "recordings");
    public static string OutputDir => Path.Combine(Root, "output");
    public static string SttCacheDir => Path.Combine(Root, "models", "stt");

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "MeetingTranscriber.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? AppContext.BaseDirectory;
    }
}
