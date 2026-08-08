using NAudio.CoreAudioApi;

namespace MeetingTranscriber.App.Services;

/// <summary>Bindable wrapper around an MMDevice for UI combo boxes.</summary>
public sealed record DeviceOption(MMDevice Device, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// Enumerates Windows audio endpoints used by the recorder:
/// rendered (playback) devices for WASAPI loopback capture of "speaker out",
/// and capture (input) devices for the microphone.
/// </summary>
public static class DeviceProvider
{
    /// <summary>All active render (playback) endpoints — valid sources for loopback capture.</summary>
    public static IReadOnlyList<MMDevice> GetPlaybackDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        return devices.ToList();
    }

    /// <summary>All active capture (input) endpoints — the microphone devices.</summary>
    public static IReadOnlyList<MMDevice> GetInputDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
        return devices.ToList();
    }

    /// <summary>Default render endpoint (what the user hears / what loopback should capture).</summary>
    public static MMDevice? GetDefaultPlaybackDevice()
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    }

    /// <summary>Default capture endpoint (the default microphone).</summary>
    public static MMDevice? GetDefaultInputDevice()
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
    }

    /// <summary>Human-readable label for a device (falls back to its ID).</summary>
    public static string DisplayName(MMDevice device)
    {
        if (!string.IsNullOrWhiteSpace(device.FriendlyName))
            return device.FriendlyName;
        return device.ID;
    }
}
