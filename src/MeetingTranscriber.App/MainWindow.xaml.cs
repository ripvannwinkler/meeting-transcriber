using System.IO;
using System.Windows;
using System.Windows.Threading;
using MeetingTranscriber.App.Services;
using NAudio.CoreAudioApi;

namespace MeetingTranscriber.App;

/// <summary>
/// Phase 1 sanity UI: pick devices, Start/Stop a live loopback+mic recording,
/// and save the mixed audio to a WAV. Full MVVM + pipeline comes in later phases.
/// </summary>
public partial class MainWindow : Window
{
    private readonly RecordingService _recorder = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private DateTime _startedAt;

    public MainWindow()
    {
        InitializeComponent();

        var playback = DeviceProvider.GetPlaybackDevices();
        foreach (var d in playback)
            LoopbackCombo.Items.Add(new DeviceItem(d, DeviceProvider.DisplayName(d)));

        var inputs = DeviceProvider.GetInputDevices();
        foreach (var d in inputs)
            MicCombo.Items.Add(new DeviceItem(d, DeviceProvider.DisplayName(d)));

        var defPlayback = DeviceProvider.GetDefaultPlaybackDevice();
        var defInput = DeviceProvider.GetDefaultInputDevice();
        LoopbackCombo.SelectedIndex = IndexOfId(playback, defPlayback);
        MicCombo.SelectedIndex = IndexOfId(inputs, defInput);

        _timer.Tick += (_, _) =>
            TimerText.Text = DateTime.Now.Subtract(_startedAt).ToString(@"mm\:ss");
        _recorder.RecordingStopped += r => Dispatcher.Invoke(() => ShowResult(r));
    }

    private static int IndexOfId(IReadOnlyList<MMDevice> list, MMDevice? match)
    {
        if (match == null)
            return 0;
        for (int i = 0; i < list.Count; i++)
            if (list[i].ID == match.ID)
                return i;
        return 0;
    }

    private void OnStartClick(object sender, RoutedEventArgs e)
    {
        var loopback = (LoopbackCombo.SelectedItem as DeviceItem)?.Device;
        var mic = (MicCombo.SelectedItem as DeviceItem)?.Device;

        _startedAt = DateTime.Now;
        _timer.Start();
        StartButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        StatusText.Text = "Recording…";

        _recorder.StartRecording(
            loopback!,
            mic,
            (float)MicGainSlider.Value,
            System.IO.Path.Combine(FindRepoRoot(), "recordings")
        );
    }

    /// <summary>Walks up from the executable to the folder containing the solution file.</summary>
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (
            dir != null
            && !File.Exists(System.IO.Path.Combine(dir.FullName, "MeetingTranscriber.slnx"))
        )
            dir = dir.Parent;
        return dir?.FullName ?? AppContext.BaseDirectory;
    }

    private void OnStopClick(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        _recorder.StopRecording();
    }

    private void ShowResult(RecordingResult r)
    {
        StartButton.IsEnabled = true;
        StopButton.IsEnabled = false;
        TimerText.Text = "";
        StatusText.Text = $"Saved: {r.Path}\nDuration: {r.DurationSeconds:F1}s";
    }

    private sealed record DeviceItem(MMDevice Device, string Name)
    {
        public override string ToString() => Name;
    }
}
