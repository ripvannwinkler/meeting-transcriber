using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using MeetingTranscriber.App.Services;

namespace MeetingTranscriber.App.ViewModels;

/// <summary>
/// Main view model: device selection, mic gain, start/stop recording through
/// <see cref="RecordingService"/>, and the transcript/summary result area
/// produced by <see cref="IConversationPipeline"/>.
/// </summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly RecordingService _recorder = new();
    private readonly IConversationPipeline _pipeline;
    private readonly Dispatcher _dispatcher = Application.Current.Dispatcher;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private DateTime _startedAt;

    private DeviceOption? _selectedPlayback;
    private DeviceOption? _selectedInput;
    private float _micGain = 1f;
    private bool _isRecording;
    private string _status = "Ready.";
    private string _timerText = "";
    private string _transcript = "";
    private string _summary = "";
    private bool _isProcessing;
    private string _recordingPath = "";
    private string? _loopbackTrack;
    private string? _micTrack;
    private InterruptedSession? _interruptedSession;

    public MainViewModel(IConversationPipeline pipeline)
    {
        _pipeline = pipeline;

        // Commands first: setters like SelectedPlayback raise CanExecuteChanged.
        StartRecordingCommand = new RelayCommand(
            _ => StartRecording(),
            _ => !IsRecording && (SelectedPlayback is not null || SelectedInput is not null)
        );
        StopRecordingCommand = new RelayCommand(_ => StopRecording(), _ => IsRecording);
        OpenSettingsCommand = new RelayCommand(_ => OpenSettings(), _ => !IsRecording);
        OpenRecordingCommand = new RelayCommand(
            _ => OpenRecording(),
            _ => !IsRecording && !IsProcessing
        );
        ResumeRecordingCommand = new RelayCommand(
            _ => ResumeRecording(),
            _ => HasInterruptedRecording && !IsRecording && !IsProcessing
        );
        TranscribeCommand = new RelayCommand(
            _ => _ = RunTranscriptAsync(),
            _ => HasRecording && !IsProcessing
        );
        SaveTranscriptCommand = new RelayCommand(_ => SaveTranscript());
        CopyTranscriptCommand = new RelayCommand(_ => CopyTranscript());
        SaveSummaryCommand = new RelayCommand(_ => SaveSummary());
        CopySummaryCommand = new RelayCommand(_ => CopySummary());

        // "None" lets the user record from just one source at a time.
        PlaybackDevices.Add(DeviceOption.None);
        foreach (var d in DeviceProvider.GetPlaybackDevices())
            PlaybackDevices.Add(new DeviceOption(d, DeviceProvider.DisplayName(d)));
        InputDevices.Add(DeviceOption.None);
        foreach (var d in DeviceProvider.GetInputDevices())
            InputDevices.Add(new DeviceOption(d, DeviceProvider.DisplayName(d)));

        SelectedPlayback = SelectDefault(
            PlaybackDevices,
            DeviceProvider.GetDefaultPlaybackDevice()
        );
        SelectedInput = SelectDefault(InputDevices, DeviceProvider.GetDefaultInputDevice());

        _timer.Tick += (_, _) => TimerText = DateTime.Now.Subtract(_startedAt).ToString(@"mm\:ss");

        // Detect a session from a previous run that was interrupted (e.g. a crash).
        _interruptedSession = RecordingService.TryGetInterruptedSession();
        if (_interruptedSession != null)
            Status =
                $"Found an interrupted recording: {_interruptedSession.WavPath} — press 'Resume Recording…' to continue it, or start a new one.";
        ResumeRecordingCommand.RaiseCanExecuteChanged();

        // These can fire from background threads (capture threads / reconnect task), so
        // marshal every update onto the UI thread.
        _recorder.RecordingStopped += r => PostToUi(() => OnRecordingStopped(r));
        _recorder.RecordingStatusChanged += msg => PostToUi(() => Status = msg);
    }

    public ObservableCollection<DeviceOption> PlaybackDevices { get; } = new();
    public ObservableCollection<DeviceOption> InputDevices { get; } = new();

    public RelayCommand StartRecordingCommand { get; }
    public RelayCommand StopRecordingCommand { get; }
    public RelayCommand OpenSettingsCommand { get; }
    public RelayCommand OpenRecordingCommand { get; }
    public RelayCommand ResumeRecordingCommand { get; }
    public RelayCommand TranscribeCommand { get; }
    public RelayCommand SaveTranscriptCommand { get; }
    public RelayCommand CopyTranscriptCommand { get; }
    public RelayCommand SaveSummaryCommand { get; }
    public RelayCommand CopySummaryCommand { get; }

    public DeviceOption? SelectedPlayback
    {
        get => _selectedPlayback;
        set
        {
            if (Set(ref _selectedPlayback, value))
                StartRecordingCommand.RaiseCanExecuteChanged();
        }
    }
    public DeviceOption? SelectedInput
    {
        get => _selectedInput;
        set
        {
            if (Set(ref _selectedInput, value))
                StartRecordingCommand.RaiseCanExecuteChanged();
        }
    }
    public float MicGain
    {
        get => _micGain;
        set => Set(ref _micGain, value);
    }

    public bool IsRecording
    {
        get => _isRecording;
        private set
        {
            if (Set(ref _isRecording, value))
            {
                StartRecordingCommand.RaiseCanExecuteChanged();
                StopRecordingCommand.RaiseCanExecuteChanged();
                OpenSettingsCommand.RaiseCanExecuteChanged();
                OpenRecordingCommand.RaiseCanExecuteChanged();
            }
        }
    }
    public string Status
    {
        get => _status;
        set => Set(ref _status, value);
    }
    public string TimerText
    {
        get => _timerText;
        set => Set(ref _timerText, value);
    }
    public string Transcript
    {
        get => _transcript;
        set
        {
            if (Set(ref _transcript, value))
            {
                SaveTranscriptCommand.RaiseCanExecuteChanged();
                CopyTranscriptCommand.RaiseCanExecuteChanged();
            }
        }
    }
    public string Summary
    {
        get => _summary;
        set
        {
            if (Set(ref _summary, value))
            {
                SaveSummaryCommand.RaiseCanExecuteChanged();
                CopySummaryCommand.RaiseCanExecuteChanged();
            }
        }
    }
    public bool IsProcessing
    {
        get => _isProcessing;
        private set
        {
            if (Set(ref _isProcessing, value))
            {
                TranscribeCommand.RaiseCanExecuteChanged();
                OpenRecordingCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasRecording => !string.IsNullOrEmpty(_recordingPath);
    public bool HasInterruptedRecording => _interruptedSession != null;
    public bool HasTranscript => !string.IsNullOrWhiteSpace(Transcript);
    public bool HasSummary => !string.IsNullOrWhiteSpace(Summary);

    private void StartRecording()
    {
        StartRecordingCore(
            SelectedPlayback?.Device,
            SelectedInput?.Device,
            MicGain,
            Paths.RecordingsDir,
            continueFromPath: null
        );
    }

    /// <summary>Continues an interrupted session into the same WAV, reusing its devices.</summary>
    private void ResumeRecording()
    {
        if (IsRecording || IsProcessing || _interruptedSession is null)
            return;

        var session = _interruptedSession;
        // A source the session recorded with is 'None' iff its id was not persisted;
        // an id that no longer resolves falls back to whatever is selected now.
        var loopback = session.LoopbackId is null
            ? null
            : FindDeviceById(PlaybackDevices, session.LoopbackId) ?? SelectedPlayback;
        var mic = session.MicId is null
            ? null
            : FindDeviceById(InputDevices, session.MicId) ?? SelectedInput;

        if (loopback is null && mic is null)
        {
            Status =
                "Cannot resume: neither original audio source is available. Pick devices and start a new recording.";
            return;
        }

        IsRecording = false; // clear interrupted flag before starting the resumed session
        StartRecordingCore(
            loopback?.Device,
            mic?.Device,
            MicGain,
            Paths.RecordingsDir,
            continueFromPath: session.WavPath
        );
    }

    private void StartRecordingCore(
        NAudio.CoreAudioApi.MMDevice? loopbackDevice,
        NAudio.CoreAudioApi.MMDevice? micDevice,
        float gain,
        string outputDir,
        string? continueFromPath
    )
    {
        if (IsRecording)
            return;

        // Consume any interrupted-session marker: starting (fresh or resume) supersedes it.
        _interruptedSession = null;
        ResumeRecordingCommand.RaiseCanExecuteChanged();

        // Don't start when neither source is selected (or available).
        if (loopbackDevice is null && micDevice is null)
        {
            Status = "Pick at least one audio source (speaker output or microphone) to record.";
            return;
        }

        Directory.CreateDirectory(Paths.RecordingsDir);
        _recordingPath = "";
        _loopbackTrack = null;
        _micTrack = null;
        Transcript = "";
        Summary = "";
        _startedAt = DateTime.Now;
        TimerText = "00:00";

        try
        {
            _timer.Start();
            IsRecording = true;
            Status = continueFromPath == null ? "Recording…" : $"Resuming… {continueFromPath}";
            _recorder.StartRecording(loopbackDevice, micDevice, gain, outputDir, continueFromPath);
        }
        catch (Exception ex)
        {
            // WASAPI init can fail (device unplugged / in use); surface it and unwind.
            _timer.Stop();
            TimerText = "";
            IsRecording = false;
            Status = "Could not start recording: " + ex.Message;
        }
    }

    private static DeviceOption? FindDeviceById(IEnumerable<DeviceOption> options, string? id)
    {
        if (string.IsNullOrEmpty(id))
            return null;
        return options.FirstOrDefault(o => o.Device?.ID == id);
    }

    private void StopRecording()
    {
        _timer.Stop();
        TimerText = "";
        _recorder.StopRecording(); // raises RecordingStopped on this (UI) thread
    }

    private void OnRecordingStopped(RecordingResult r)
    {
        IsRecording = false;
        _recordingPath = r.Path;
        _loopbackTrack = r.LoopbackTrack;
        _micTrack = r.MicTrack;
        // A clean stop clears any interrupted-session state.
        _interruptedSession = null;
        ResumeRecordingCommand.RaiseCanExecuteChanged();
        TranscribeCommand.RaiseCanExecuteChanged();

        var parts = new List<string>();
        if (r.LoopbackTrack is not null)
            parts.Add($"speaker signal {r.LoopbackSignalPct}%");
        if (r.MicTrack is not null)
            parts.Add($"mic signal {r.MicSignalPct}%");
        var detail = parts.Count > 0 ? "  [" + string.Join(" | ", parts) + "]" : "";
        // Low-signal warnings only apply to sources the user actually recorded from.
        var lowSignal =
            (r.LoopbackTrack is not null && r.LoopbackSignalPct < 25)
            || (r.MicTrack is not null && r.MicSignalPct < 25);
        var baseline = r.Interrupted
            ? $"Saved: {r.Path} ({r.DurationSeconds:F1}s) — an audio stream dropped during recording, so there is a gap in the audio."
            : $"Saved: {r.Path} ({r.DurationSeconds:F1}s)";
        Status =
            $"{baseline}{detail}"
            + (
                lowSignal
                    ? $"  Low signal! Details: {System.IO.Path.Combine(Paths.RecordingsDir, "recording_debug.log")}"
                    : ""
            );
    }

    /// <summary>
    /// Loads an existing WAV (e.g. from a previous session after a restart) so it can be
    /// transcribed and summarized without re-recording.
    /// </summary>
    private void OpenRecording()
    {
        if (IsRecording || IsProcessing)
            return;

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Open a recording",
            Filter = "WAV files (*.wav)|*.wav|All files (*.*)|*.*",
            InitialDirectory = Paths.RecordingsDir,
        };
        if (dialog.ShowDialog() != true)
            return;

        _recordingPath = dialog.FileName;
        _loopbackTrack = null;
        _micTrack = null;
        Transcript = "";
        Summary = "";
        TimerText = "";
        Status = $"Loaded: {dialog.FileName}";
        TranscribeCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Posts an action to the UI thread, tolerating dispatcher shutdown.</summary>
    private void PostToUi(Action action)
    {
        try
        {
            _dispatcher.BeginInvoke(action);
        }
        catch
        {
            // App is shutting down; drop the update.
        }
    }

    private async Task RunTranscriptAsync()
    {
        if (string.IsNullOrEmpty(_recordingPath) || IsProcessing)
            return;
        IsProcessing = true;
        try
        {
            Transcript = "";
            Summary = "";
            Status = "Transcribing…";
            var progress = new Progress<string>(m => Status = m);
            Transcript = (
                await _pipeline.TranscribeAsync(
                    _recordingPath,
                    progress,
                    CancellationToken.None,
                    _loopbackTrack,
                    _micTrack
                )
            ).Trim();

            Status = "Summarizing…";
            Summary = (await _pipeline.SummarizeAsync(Transcript, progress)).Trim();
            Status = "Done.";
        }
        catch (Exception ex)
        {
            Status = "Pipeline error: " + ex.Message;
        }
        finally
        {
            IsProcessing = false;
        }
    }

    private void OpenSettings()
    {
        var owner = Application.Current.MainWindow;
        var window = new SettingsWindow { Owner = owner };
        window.ShowDialog();
    }

    private void SaveTranscript() => SaveText(Transcript, "transcript.txt");

    private void SaveSummary() => SaveText(Summary, "summary.txt");

    private void CopyTranscript() => Clipboard.SetText(Transcript);

    private void CopySummary() => Clipboard.SetText(Summary);

    private void SaveText(string text, string defaultName)
    {
        if (string.IsNullOrEmpty(text))
            return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            InitialDirectory = Paths.OutputDir,
            FileName =
                Path.GetFileNameWithoutExtension(_recordingPath)
                + "_"
                + Path.GetFileName(defaultName),
        };
        if (dialog.ShowDialog() == true)
            File.WriteAllText(dialog.FileName, text);
    }

    private static DeviceOption? SelectDefault(
        IEnumerable<DeviceOption> options,
        NAudio.CoreAudioApi.MMDevice? match
    )
    {
        if (match != null)
        {
            foreach (var option in options)
                if (option.Device?.ID == match.ID)
                    return option;
        }
        return options.FirstOrDefault();
    }

    public void Dispose()
    {
        // Stops any in-progress capture when the window closes.
        _recorder.Dispose();
        _timer.Stop();
    }
}
