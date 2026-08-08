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
public sealed class MainViewModel : ObservableObject
{
    private readonly RecordingService _recorder = new();
    private readonly IConversationPipeline _pipeline;
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

    public MainViewModel(IConversationPipeline pipeline)
    {
        _pipeline = pipeline;

        // Commands first: setters like SelectedPlayback raise CanExecuteChanged.
        StartRecordingCommand = new RelayCommand(
            _ => StartRecording(),
            _ => !IsRecording && SelectedPlayback is not null
        );
        StopRecordingCommand = new RelayCommand(_ => StopRecording(), _ => IsRecording);
        OpenSettingsCommand = new RelayCommand(_ => OpenSettings(), _ => !IsRecording);
        TranscribeCommand = new RelayCommand(
            _ => _ = RunTranscriptAsync(),
            _ => HasRecording && !IsProcessing
        );
        SaveTranscriptCommand = new RelayCommand(_ => SaveTranscript());
        CopyTranscriptCommand = new RelayCommand(_ => CopyTranscript());
        SaveSummaryCommand = new RelayCommand(_ => SaveSummary());
        CopySummaryCommand = new RelayCommand(_ => CopySummary());

        foreach (var d in DeviceProvider.GetPlaybackDevices())
            PlaybackDevices.Add(new DeviceOption(d, DeviceProvider.DisplayName(d)));
        foreach (var d in DeviceProvider.GetInputDevices())
            InputDevices.Add(new DeviceOption(d, DeviceProvider.DisplayName(d)));

        SelectedPlayback = SelectDefault(
            PlaybackDevices,
            DeviceProvider.GetDefaultPlaybackDevice()
        );
        SelectedInput = SelectDefault(InputDevices, DeviceProvider.GetDefaultInputDevice());

        _timer.Tick += (_, _) => TimerText = DateTime.Now.Subtract(_startedAt).ToString(@"mm\:ss");
        _recorder.RecordingStopped += r => OnRecordingStopped(r);
    }

    public ObservableCollection<DeviceOption> PlaybackDevices { get; } = new();
    public ObservableCollection<DeviceOption> InputDevices { get; } = new();

    public RelayCommand StartRecordingCommand { get; }
    public RelayCommand StopRecordingCommand { get; }
    public RelayCommand OpenSettingsCommand { get; }
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
        set => Set(ref _selectedInput, value);
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
                TranscribeCommand.RaiseCanExecuteChanged();
        }
    }

    public bool HasRecording => !string.IsNullOrEmpty(_recordingPath);
    public bool HasTranscript => !string.IsNullOrWhiteSpace(Transcript);
    public bool HasSummary => !string.IsNullOrWhiteSpace(Summary);

    private void StartRecording()
    {
        if (IsRecording)
            return;
        Directory.CreateDirectory(Paths.RecordingsDir);
        _recordingPath = "";
        Transcript = "";
        Summary = "";
        _startedAt = DateTime.Now;
        TimerText = "00:00";
        _timer.Start();
        IsRecording = true;
        Status = "Recording…";

        _recorder.StartRecording(
            SelectedPlayback!.Device,
            SelectedInput?.Device,
            MicGain,
            Paths.RecordingsDir
        );
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
        Status = $"Saved: {r.Path} ({r.DurationSeconds:F1}s)";
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
            Transcript = (await _pipeline.TranscribeAsync(_recordingPath, progress)).Trim();

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
                if (option.Device.ID == match.ID)
                    return option;
        }
        return options.FirstOrDefault();
    }
}
