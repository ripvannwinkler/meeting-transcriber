using System.Diagnostics;
using System.IO;
using System.Text.Json;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MeetingTranscriber.App.Services;

/// <summary>Result of a completed recording session.</summary>
public sealed record RecordingResult(
    string Path,
    float DurationSeconds,
    bool Interrupted = false,
    int LoopbackSignalPct = 0,
    int MicSignalPct = 0,
    string? LoopbackTrack = null,
    string? MicTrack = null
);

/// <summary>An interrupted recording left by a previous app run, recoverable via resume.</summary>
public sealed record InterruptedSession(string WavPath, string? LoopbackId, string? MicId);

/// <summary>
/// Captures system audio (WASAPI loopback = "speaker out") and an optional
/// microphone simultaneously, mixes both into a single 48 kHz stereo stream via
/// <see cref="AudioMixer"/>, and writes it to a WAV file on a background thread.
///
/// Resiliency: if a capture's stream dies mid-session (device unplugged, audio
/// session lost, glitch), the source is re-opened by device ID with bounded
/// retries. During the outage the surviving stream keeps recording (with a
/// silence gap); if all configured sources are permanently lost the session
/// finalizes itself. Every transition is surfaced via <see cref="RecordingStatusChanged"/>.
/// </summary>
public sealed class RecordingService : IDisposable
{
    private const int MaxReconnectAttempts = 5;
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(1);
    private const string SessionMarkerFileName = ".session.json";

    private enum SourceKind
    {
        Loopback,
        Mic,
    }

    private sealed class SourceState
    {
        public required SourceKind Kind;
        public required string DeviceId;
        public required float Gain;
        public required bool Configured;
        public volatile bool Alive; // a capture is currently feeding this source
        public volatile bool Reconnecting; // a reconnect attempt is in flight
        public int LossCount; // times this source was lost this session (under _sync)
        public AudioMixer.Source? MixerSource;
        public WaveFormat? Format;
    }

    private static readonly WaveFormat OutputFormat = new(48000, 16, 2);
    private static readonly WaveFormat TrackFormat = new(48000, 16, 1);

    private readonly object _sync = new();
    private AudioMixer? _mixer;
    private WavSink? _sink;
    private WavSink? _loopbackTrackSink;
    private WavSink? _micTrackSink;
    private string _loopbackTrackPath = string.Empty;
    private string? _micTrackPath;
    private Thread? _mixThread;
    private WasapiCapture? _loopback;
    private WasapiCapture? _mic;
    private SourceState? _loopbackState;
    private SourceState? _micState;
    private volatile bool _running;
    private volatile bool _stopping;
    private volatile bool _stopRequested;
    private volatile bool _sawUnexpectedLoss;
    private long _lastNonZeroSample = -1;
    private long _preexistingSamples = 0;
    private string _debugLogPath = string.Empty;
    private long _lastDiagSec = -1;
    private readonly float[] _trackFloat = new float[4800];
    private readonly short[] _trackShort = new short[4800];
    private readonly byte[] _trackByte = new byte[9600];
    private string _outputPath = string.Empty;

    public bool IsRecording => _running;

    /// <summary>Raised (on the UI thread) when recording ends with the result.</summary>
    public event Action<RecordingResult>? RecordingStopped;

    /// <summary>Raised (on a background thread) for live status messages during the session.</summary>
    public event Action<string>? RecordingStatusChanged;

    public void StartRecording(
        MMDevice loopbackDevice,
        MMDevice? micDevice,
        float micGain,
        string outputDir,
        string? continueFromPath = null
    )
    {
        lock (_sync)
        {
            if (_running)
                return;

            Directory.CreateDirectory(outputDir);
            _outputPath =
                continueFromPath
                ?? System.IO.Path.Combine(
                    outputDir,
                    DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".wav"
                );
            _debugLogPath = System.IO.Path.Combine(outputDir, "recording_debug.log");
            _mixer = new AudioMixer(48000, 2);
            _sawUnexpectedLoss = false;
            _stopping = false;
            _stopRequested = false;

            _loopbackState = new SourceState
            {
                Kind = SourceKind.Loopback,
                DeviceId = loopbackDevice.ID,
                Gain = 0.85f,
                Configured = true,
            };

            if (micDevice != null)
            {
                _micState = new SourceState
                {
                    Kind = SourceKind.Mic,
                    DeviceId = micDevice.ID,
                    Gain = Math.Clamp(micGain, 0f, 4f),
                    Configured = true,
                };
            }

            try
            {
                LaunchCapture(_loopbackState, loopbackDevice);
                if (_micState != null)
                    LaunchCapture(_micState, micDevice!);

                _sink = new WavSink(_outputPath, OutputFormat, append: continueFromPath != null);
                _preexistingSamples = _sink.PreexistingSamples;

                // Sidecar per-source mono tracks for dual-stream transcription.
                var baseName = System.IO.Path.ChangeExtension(_outputPath, null);
                _loopbackTrackPath = baseName + "_loopback.wav";
                _loopbackTrackSink = new WavSink(
                    _loopbackTrackPath,
                    TrackFormat,
                    append: continueFromPath != null
                );
                if (_micState != null)
                {
                    _micTrackPath = baseName + "_mic.wav";
                    _micTrackSink = new WavSink(
                        _micTrackPath,
                        TrackFormat,
                        append: continueFromPath != null
                    );
                }

                WriteSessionMarker(loopbackDevice.ID, micDevice?.ID);
                Log(
                    $"session start | wav={_outputPath} append={continueFromPath != null} | "
                        + $"loopback={loopbackDevice.FriendlyName} [{loopbackDevice.ID}] fmt={(_loopbackState?.Format?.SampleRate ?? 0)}/{_loopbackState?.Format?.Channels ?? 0} get->{_loopbackState?.Format?.Encoding}"
                        + (
                            micDevice != null
                                ? $" | mic={micDevice.FriendlyName} [{micDevice.ID}] fmt={(_micState?.Format?.SampleRate ?? 0)}/{_micState?.Format?.Channels ?? 0}"
                                : " | mic=none"
                        )
                );
                var chunk = new float[9600]; // 100 ms of 48 kHz stereo

                _mixThread = new Thread(() => MixLoop(chunk))
                {
                    IsBackground = true,
                    Name = "MeetingMixer",
                };

                _loopbackState!.Alive = true;
                if (_micState is { } micState)
                    micState.Alive = true;

                _running = true;
                _mixThread.Start();
            }
            catch
            {
                // Initial device init failed (e.g. endpoint vanished between selection
                // and start): cleanup partial state and let the caller surface the error.
                _loopback?.Dispose();
                _mic?.Dispose();
                _loopback = null;
                _mic = null;
                _mixer?.Dispose();
                _mixer = null;
                _loopbackState = null;
                _micState = null;
                _running = false;
                throw;
            }
        }
    }

    public RecordingResult? StopRecording()
    {
        lock (_sync)
        {
            if (!_running)
                return null;
            return CompleteSession();
        }
    }

    /// <summary>
    /// Ends the session (called for both user stop and total-stream loss).
    /// Caller must hold <see cref="_sync"/>.
    /// </summary>
    private RecordingResult CompleteSession()
    {
        _stopping = true; // suppresses reconnect logic in RecordingStopped handlers
        _stopRequested = true;

        try
        {
            _loopback?.StopRecording();
        }
        catch
        { /* ignore */
        }
        try
        {
            _mic?.StopRecording();
        }
        catch
        { /* ignore */
        }

        // Explicitly complete sources so IsDrained resolves even if a
        // RecordingStopped callback never fired.
        if (_loopbackState?.MixerSource != null)
            _mixer?.CompleteSource(_loopbackState.MixerSource);
        if (_micState?.MixerSource != null)
            _mixer?.CompleteSource(_micState.MixerSource);

        _mixThread?.Join(TimeSpan.FromSeconds(15));
        _mixThread = null;

        var path = _outputPath;
        int bytesPerSecond =
            OutputFormat.SampleRate * OutputFormat.Channels * (OutputFormat.BitsPerSample / 8);
        var duration = 0f;
        if (_sink != null)
        {
            _sink.Dispose();
            _sink = null;
        }
        DeleteSessionMarker();

        // Trim the trailing silence the resampler padded the recording with.
        if (_lastNonZeroSample >= 0)
            TrimWaveFile(path, (_lastNonZeroSample + 1) * 2);

        // Report duration from the final trimmed file (not the pacer's padded length).
        if (File.Exists(path))
            duration = new FileInfo(path).Length / (float)bytesPerSecond;

        // Capture per-source coverage before the mixer/state is torn down.
        var loopbackPct = SignalPct(_loopbackState?.MixerSource);
        var micPct = SignalPct(_micState?.MixerSource);
        var loopArr = _loopbackState?.MixerSource?.ArrivedSamples ?? 0;
        var micArr = _micState?.MixerSource?.ArrivedSamples ?? 0;

        _loopback?.Dispose();
        _loopback = null;
        _mic?.Dispose();
        _mic = null;
        _mixer?.Dispose();
        _mixer = null;
        _loopbackState = null;
        _micState = null;
        _running = false;
        // NOTE: _stopping intentionally stays true until the next session explicitly
        // begins (StartRecording resets it). This prevents any RecordingStopped handler
        // that was blocked on _sync during this shutdown from waking up afterwards and
        // mistaking the just-stopped capture for an unexpected loss (which would start
        // spurious reconnects).

        var result = new RecordingResult(
            path,
            duration,
            Interrupted: _sawUnexpectedLoss,
            LoopbackSignalPct: loopbackPct,
            MicSignalPct: micPct,
            LoopbackTrack: _loopbackTrackPath.Length > 0 ? _loopbackTrackPath : null,
            MicTrack: _micTrackPath
        );
        Log(
            $"stop | dur={duration:F1}s bytes={new FileInfo(path).Length} interrupted={_sawUnexpectedLoss} "
                + $"loopback sig%={loopbackPct} (arr={loopArr}) "
                + $"mic sig%={micPct} (arr={micArr})"
        );
        Log($"tracks: loop={_loopbackTrackPath} mic={_micTrackPath ?? "none"}");
        RecordingStopped?.Invoke(result);
        return result;
    }

    // ---------------- capture lifecycle / reconnect ----------------

    /// <summary>
    /// Creates the capture for <paramref name="state"/>, subscribes its handlers,
    /// wires it into the mixer (reusing the source if the format is unchanged), and
    /// starts it. Throws and cleans up on failure.
    /// </summary>
    private void LaunchCapture(SourceState state, MMDevice device)
    {
        var previous = GetCapture(state.Kind);
        try
        {
            WasapiCapture capture =
                state.Kind == SourceKind.Loopback
                    ? new WasapiLoopbackCapture(device)
                    // event-sync avoids the NAudio burst-read crash on some mic devices
                    : new WasapiCapture(device, useEventSync: true);
            SetCapture(state.Kind, capture);

            var format = capture.WaveFormat;
            AudioMixer.Source source;
            lock (_sync)
            {
                if (state.MixerSource != null && FormatsCompatible(state.Format!, format))
                {
                    source = state.MixerSource;
                }
                else
                {
                    // Format changed (e.g. different sample rate after reconnect):
                    // start a new mixer stream; the old one is finished.
                    if (state.MixerSource != null)
                        _mixer!.CompleteSource(state.MixerSource);
                    source = _mixer!.AddSource(
                        format,
                        state.Gain,
                        role: state.Kind == SourceKind.Loopback
                            ? AudioMixer.SourceRole.Loopback
                            : AudioMixer.SourceRole.Mic
                    );
                    state.MixerSource = source;
                }
                state.Format = format;
            }

            var sourceRef = source;
            capture.DataAvailable += (_, e) =>
            {
                var floats = NormalizeToFloat(format, e.Buffer, e.BytesRecorded);
                _mixer?.Push(sourceRef.Id, floats, 0, floats.Length);
            };
            capture.RecordingStopped += (_, e) => OnCaptureStopped(state, e);
            capture.StartRecording();

            // Superseded capture (e.g. the one that died and triggered this reconnect) is
            // now finished and can be released. Not disposed until success so the failure
            // path can restore it intact.
            if (previous != null)
            {
                try
                {
                    previous.Dispose();
                }
                catch
                { /* ignore */
                }
            }
        }
        catch
        {
            var capture = GetCapture(state.Kind);
            if (capture != null)
                capture.Dispose();
            SetCapture(state.Kind, previous);
            throw;
        }
    }

    private void OnCaptureStopped(SourceState state, StoppedEventArgs args)
    {
        lock (_sync)
        {
            if (_stopping)
            {
                _mixer?.CompleteSource(state.MixerSource!);
                return;
            }
            state.Alive = false;
            state.LossCount++;
        }

        _sawUnexpectedLoss = true;
        state.Reconnecting = true;
        var label = state.Kind == SourceKind.Loopback ? "loopback" : "mic";
        Log($"stream lost: {label} ex={(args.Exception?.Message ?? "none")}");
        RecordingStatusChanged?.Invoke(
            $"{(state.Kind == SourceKind.Loopback ? "Speaker output" : "Microphone")} stream lost — reconnecting…"
        );
        _ = Task.Run(() => ReconnectAsync(state));
    }

    private async Task ReconnectAsync(SourceState state)
    {
        var label = state.Kind == SourceKind.Loopback ? "Speaker output" : "Microphone";
        for (int attempt = 1; attempt <= MaxReconnectAttempts; attempt++)
        {
            if (_stopping)
                return;

            // Bound by total losses this session, not just consecutive failures: a
            // capture that re-opens but dies immediately again would otherwise retry forever.
            if (state.LossCount > MaxReconnectAttempts)
                break;
            if (attempt == 1 || attempt == MaxReconnectAttempts)
                RecordingStatusChanged?.Invoke(
                    $"{label} unavailable (reconnect {attempt}/{MaxReconnectAttempts})…"
                );

            MMDevice? device;
            try
            {
                device = FindDeviceById(state.DeviceId);
            }
            catch
            {
                device = null;
            }

            if (device == null || _stopping)
            {
                await Task.Delay(ReconnectDelay);
                continue;
            }

            try
            {
                lock (_sync)
                {
                    if (_stopping)
                        return;
                    LaunchCapture(state, device);
                    state.Alive = true;
                    state.Reconnecting = false;
                }
                RecordingStatusChanged?.Invoke($"{label} reconnected.");
                Log($"reconnected: {label}");
                return;
            }
            catch
            {
                // Endpoint not ready yet (device still unplugged / busy) — retry.
            }

            await Task.Delay(ReconnectDelay);
        }

        // Permanent loss of this source.
        bool allGone;
        lock (_sync)
        {
            state.Reconnecting = false;
            _mixer?.CompleteSource(state.MixerSource!);
            bool anyOtherAlive =
                (_loopbackState?.Alive == true || _loopbackState?.Reconnecting == true)
                || (_micState?.Alive == true || _micState?.Reconnecting == true);
            allGone = !anyOtherAlive;
        }

        if (allGone)
        {
            RecordingStatusChanged?.Invoke("All audio streams lost — stopping recording.");
            Log("all sources lost — auto-stopping");
            lock (_sync)
            {
                if (_running)
                    CompleteSession();
            }
        }
        else
        {
            RecordingStatusChanged?.Invoke(
                $"{label} unavailable — recording continues with the remaining source."
            );
            Log($"gave up reconnect: {label}");
        }
    }

    private void SetCapture(SourceKind kind, WasapiCapture? capture)
    {
        if (kind == SourceKind.Loopback)
            _loopback = capture;
        else
            _mic = capture;
    }

    private WasapiCapture? GetCapture(SourceKind kind) =>
        kind == SourceKind.Loopback ? _loopback : _mic;

    private static MMDevice? FindDeviceById(string id)
    {
        using var enumerator = new MMDeviceEnumerator();
        foreach (
            var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
        )
        {
            if (device.ID == id)
                return device;
        }
        foreach (
            var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
        )
        {
            if (device.ID == id)
                return device;
        }
        return null;
    }

    private static bool FormatsCompatible(WaveFormat a, WaveFormat b) =>
        a.SampleRate == b.SampleRate
        && a.Channels == b.Channels
        && a.Encoding == b.Encoding
        && a.BitsPerSample == b.BitsPerSample;

    // ---------------- mix thread ----------------

    private void MixLoop(float[] chunk)
    {
        var shortBuf = new short[chunk.Length];
        var byteBuf = new byte[chunk.Length * 2];
        long totalSamples = _preexistingSamples;
        // Absolute (whole-file) last non-zero sample index for trailing-silence trim, so a
        // resume never trims away the pre-existing portion of the file.
        _lastNonZeroSample = _preexistingSamples - 1;

        // Pacing: the recorder must write audio at exactly real time. If input buffers
        // deliver in bursts (WASAPI/WDL resampler jitter), reading greedily every loop
        // can consume ~8x real time and time-stretch the WAV (which makes Whisper return
        // an empty transcript). So we bound each read to the wall clock's progress.
        long samplesPerSecond = OutputFormat.SampleRate * OutputFormat.Channels; // 96000
        var clock = Stopwatch.StartNew();
        long writtenSamples = 0;
        long drainedSinceMs = -1;
        long lastHeaderPatchMs = 0;

        while (true)
        {
            // Crash-safety: periodically finalize the WAV header on disk so that if the
            // process dies mid-recording, the file up to that point is still readable.
            if (_sink != null && clock.Elapsed.TotalMilliseconds - lastHeaderPatchMs >= 5000)
            {
                lastHeaderPatchMs = (long)clock.Elapsed.TotalMilliseconds;
                try
                {
                    _sink.PatchHeader();
                    TouchSessionMarker();
                }
                catch
                {
                    // Patching is best-effort; recording continues either way.
                }
            }

            bool drained = _stopRequested && _mixer!.IsDrained;

            int budget;
            if (drained)
                budget = chunk.Length; // short flush window after input drains
            else
                budget = (int)
                    Math.Clamp(
                        (long)(clock.Elapsed.TotalSeconds * samplesPerSecond) - writtenSamples,
                        0,
                        chunk.Length
                    );

            // If we're exactly at (or ahead of) real time, wait for the clock to catch up.
            if (!drained && budget == 0)
            {
                Thread.Sleep(5);
                continue;
            }

            var count = _mixer!.ReadMix(chunk, budget);
            if (count > 0)
            {
                count -= count % 2; // keep frame-aligned for 16-bit stereo
                if (count > 0)
                {
                    for (int i = 0; i < count; i++)
                    {
                        var v = chunk[i] * 32767f;
                        var s = (short)(
                            v > 32767f ? 32767
                            : v < -32768f ? -32768
                            : (short)v
                        );
                        shortBuf[i] = s;
                        if (s != 0)
                            _lastNonZeroSample = totalSamples + i;
                    }
                    Buffer.BlockCopy(shortBuf, 0, byteBuf, 0, count * 2);
                    _sink!.WriteData(byteBuf, count * 2);
                    totalSamples += count;
                    writtenSamples += count;
                }

                // Sidecar mono tracks (same real-time pacing; frames = samples/2).
                WriteTracks(budget / 2);
            }

            // The resampler streams an endless tail of zeros once its input drains, so
            // ReadMix never returns 0. Once stopping is requested and every input buffer
            // is empty, keep pulling for a short bounded window to flush the resampler's
            // real signal tail, then finish and trim the trailing zeros that padded it.
            if (drained)
            {
                if (drainedSinceMs < 0)
                    drainedSinceMs = (long)clock.Elapsed.TotalMilliseconds;
                if (clock.Elapsed.TotalMilliseconds - drainedSinceMs >= 300) // ~300 ms flush
                    break;
            }
            else
            {
                drainedSinceMs = -1;
            }

            Thread.Sleep(2);

            // Diagnostics: one line per second with per-source levels and buffer state.
            {
                var sec = (long)clock.Elapsed.TotalSeconds;
                if (sec != _lastDiagSec)
                {
                    _lastDiagSec = sec;
                    var sb = new System.Text.StringBuilder();
                    sb.Append("sec=")
                        .Append(sec)
                        .Append(" t=")
                        .Append(clock.Elapsed.TotalSeconds.ToString("F1"));
                    AppendSourceDiag(sb, "loop", _loopbackState?.MixerSource);
                    AppendSourceDiag(sb, "mic", _micState?.MixerSource);
                    Log(sb.ToString());

                    var loopPeak = _loopbackState?.MixerSource?.LastPeak ?? 0f;
                    var micPeak = _micState?.MixerSource?.LastPeak ?? 0f;
                    RecordingStatusChanged?.Invoke(
                        $"Recording… (t={sec}s)  speaker {loopPeak:F2}  |  mic {micPeak:F2}"
                    );
                }
            }
        }

        _sink?.Flush();
        _loopbackTrackSink?.Flush();
        _micTrackSink?.Flush();
        Log($"mix thread ended (written={writtenSamples})");
    }

    /// <summary>Writes the per-source mono sidecar tracks, paced by the same budget.</summary>
    private void WriteTracks(int maxFrames)
    {
        if (_loopbackTrackSink != null && _loopbackState?.MixerSource is { } loopSrc)
            WriteTrack(_loopbackTrackSink, loopSrc, maxFrames);
        if (_micTrackSink != null && _micState?.MixerSource is { } micSrc)
            WriteTrack(_micTrackSink, micSrc, maxFrames);
    }

    private void WriteTrack(WavSink sink, AudioMixer.Source source, int maxSamples)
    {
        if (maxSamples > _trackFloat.Length)
            maxSamples = _trackFloat.Length;
        if (maxSamples <= 0)
            return;

        var read = _mixer!.ReadTrack(source, _trackFloat, maxSamples);
        if (read <= 0)
            return;

        for (int i = 0; i < read; i++)
        {
            var v = _trackFloat[i] * 32767f;
            _trackShort[i] = (short)(
                v > 32767f ? 32767
                : v < -32768f ? -32768
                : (short)v
            );
        }
        Buffer.BlockCopy(_trackShort, 0, _trackByte, 0, read * 2);
        sink.WriteData(_trackByte, read * 2);
    }

    private static void AppendSourceDiag(
        System.Text.StringBuilder sb,
        string name,
        AudioMixer.Source? s
    )
    {
        sb.Append(" | ")
            .Append(name)
            .Append(" peak=")
            .Append(s?.LastPeak.ToString("F3") ?? "-")
            .Append(" sig%=")
            .Append(SignalPct(s))
            .Append(" arr=")
            .Append(s?.ArrivedSamples ?? 0)
            .Append(" buf=")
            .Append(s?.Buffer?.BufferedBytes ?? 0);
        if (s?.Buffer != null)
        {
            var cap = (long)(
                s.Buffer.BufferDuration.TotalSeconds * s.Buffer.WaveFormat.AverageBytesPerSecond
            );
            if (s.Buffer.BufferedBytes >= cap)
                sb.Append(" FULL");
        }
    }

    /// <summary>
    /// Rewrites the RIFF/data chunk sizes in the header to match the file's current
    /// length (no truncation). Used periodically during recording so a crash leaves
    /// a readable WAV; the caller should restore the stream position afterwards.
    /// </summary>
    private static void PatchWaveHeader(FileStream fs)
    {
        if (fs.Length < 12)
            return;

        long pos = 12;
        var chunkHeader = new byte[8];
        while (pos + 8 <= fs.Length)
        {
            fs.Seek(pos, SeekOrigin.Begin);
            fs.ReadExactly(chunkHeader, 0, 8);
            uint tag = BitConverter.ToUInt32(chunkHeader, 0);
            uint size = BitConverter.ToUInt32(chunkHeader, 4);
            if (tag == 0x61746164u) // "data"
            {
                var fileLength = fs.Length;
                fs.Seek(4, SeekOrigin.Begin);
                fs.Write(BitConverter.GetBytes(fileLength - 8), 0, 4);
                fs.Seek(pos + 4, SeekOrigin.Begin);
                fs.Write(BitConverter.GetBytes(fileLength - (pos + 8)), 0, 4);
                fs.Flush();
                return;
            }
            pos += 8 + size + (size % 2);
        }
    }

    /// <summary>
    /// Trims a writer-finalized 16-bit WAV to <paramref name="dataBytes"/> of PCM data and
    /// fixes its RIFF/data header sizes. Walks the chunk layout rather than assuming a
    /// fixed 44-byte header (NAudio's fmt chunk size can vary). Used to drop the trailing
    /// silence the resampler pads the recording with.
    /// </summary>
    private static void TrimWaveFile(string path, long dataBytes)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
            if (fs.Length < 12)
                return;

            // Walk chunks from offset 12 to find the data chunk's size field & start.
            long pos = 12;
            long dataSizeField = -1;
            long dataStart = -1;
            var chunkHeader = new byte[8];
            while (pos + 8 <= fs.Length)
            {
                fs.Seek(pos, SeekOrigin.Begin);
                fs.ReadExactly(chunkHeader, 0, 8);
                uint tag = BitConverter.ToUInt32(chunkHeader, 0);
                uint size = BitConverter.ToUInt32(chunkHeader, 4);
                if (tag == 0x61746164u) // "data"
                {
                    dataStart = pos + 8;
                    dataSizeField = pos + 4;
                    break;
                }
                pos += 8 + size + (size % 2); // chunks are 2-byte aligned
            }

            if (dataSizeField < 0)
                return;

            long newLength = dataStart + dataBytes;
            if (newLength >= fs.Length || newLength < 44)
                return;
            fs.SetLength(newLength);

            var riffSize = BitConverter.GetBytes((uint)(newLength - 8));
            var dataSize = BitConverter.GetBytes((uint)dataBytes);
            fs.Seek(4, SeekOrigin.Begin);
            fs.Write(riffSize, 0, 4);
            fs.Seek(dataSizeField, SeekOrigin.Begin);
            fs.Write(dataSize, 0, 4);
        }
        catch
        {
            // Never fail the stop path over cosmetic trailing-silence cleanup.
        }
    }

    /// <summary>
    /// Converts a raw capture buffer into IEEE-float bytes matching the source's
    /// sample rate/channels, so it can be pushed straight into the mixer's float buffer.
    /// </summary>
    private static byte[] NormalizeToFloat(WaveFormat format, byte[] data, int count)
    {
        if (format.Encoding == WaveFormatEncoding.IeeeFloat)
        {
            var slice = new byte[count];
            Buffer.BlockCopy(data, 0, slice, 0, count);
            return slice;
        }

        if (format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample == 16)
        {
            var floats = new float[count / 2];
            for (int i = 0; i < floats.Length; i++)
            {
                var sample = (short)(data[i * 2] | (data[i * 2 + 1] << 8));
                floats[i] = sample / 32768f;
            }
            var bytes = new byte[floats.Length * 4];
            Buffer.BlockCopy(floats, 0, bytes, 0, bytes.Length);
            return bytes;
        }

        throw new NotSupportedException(
            $"Unsupported capture format: {format.Encoding} / {format.BitsPerSample}-bit."
        );
    }

    // ---------------- session marker (crash recovery) ----------------

    private static string SessionMarkerPath =>
        System.IO.Path.Combine(Paths.RecordingsDir, SessionMarkerFileName);

    /// <summary>Appends a line to the per-session debug log (best-effort).</summary>
    private void Log(string line)
    {
        if (_debugLogPath.Length == 0)
            return;
        try
        {
            File.AppendAllText(
                _debugLogPath,
                DateTime.Now.ToString("HH:mm:ss.fff") + " " + line + Environment.NewLine
            );
        }
        catch
        {
            // logging is best-effort
        }
    }

    private static int SignalPct(AudioMixer.Source? source)
    {
        if (source is null || source.ArrivedSamples <= 0)
            return 0;
        return (int)Math.Round(100.0 * source.SignalSamples / source.ArrivedSamples);
    }

    /// <summary>
    /// Returns the interrupted recording left by a previous app run (created at session
    /// start, deleted on a clean stop), or null if the last session finished normally.
    /// </summary>
    public static InterruptedSession? TryGetInterruptedSession()
    {
        try
        {
            var path = SessionMarkerPath;
            if (!File.Exists(path))
                return null;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            var wav = root.TryGetProperty("wav", out var w) ? w.GetString() : null;
            if (string.IsNullOrEmpty(wav) || !File.Exists(wav))
                return null; // stale marker whose file is gone - ignore it

            var loopbackId = root.TryGetProperty("loopbackId", out var l) ? l.GetString() : null;
            var micId = root.TryGetProperty("micId", out var m) ? m.GetString() : null;
            return new InterruptedSession(wav, loopbackId, micId);
        }
        catch
        {
            return null;
        }
    }

    private void WriteSessionMarker(string loopbackId, string? micId)
    {
        try
        {
            Directory.CreateDirectory(Paths.RecordingsDir);
            var data = JsonSerializer.Serialize(
                new
                {
                    wav = _outputPath,
                    loopbackId,
                    micId,
                    startedAt = DateTime.UtcNow.ToString("o"),
                }
            );
            File.WriteAllText(SessionMarkerPath, data);
        }
        catch
        {
            // Marking is best-effort; recording continues regardless.
        }
    }

    private void TouchSessionMarker()
    {
        try
        {
            File.SetLastWriteTimeUtc(SessionMarkerPath, DateTime.UtcNow);
        }
        catch
        {
            // best-effort
        }
    }

    private void DeleteSessionMarker()
    {
        try
        {
            if (File.Exists(SessionMarkerPath))
                File.Delete(SessionMarkerPath);
        }
        catch
        {
            // best-effort
        }
    }

    // ---------------- WAV sink ----------------

    /// <summary>
    /// Minimal WAV writer. Unlike NAudio's WaveFileWriter (which writes a fresh header at
    /// position 0 and can't append), this opens a file either fresh or in append mode so a
    /// crashed session can be resumed into the same file. The header is patched on stop and
    /// periodically by the caller (see <see cref="PatchHeader"/>).
    /// </summary>
    private sealed class WavSink : IDisposable
    {
        private readonly FileStream _stream;

        public WavSink(string path, WaveFormat format, bool append)
        {
            if (append)
            {
                _stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
                var dataStart = FindDataStart(_stream);
                PreexistingSamples = (_stream.Length - dataStart) / 2;
                _stream.Seek(0, SeekOrigin.End); // continue after existing audio
            }
            else
            {
                _stream = new FileStream(
                    path,
                    FileMode.Create,
                    FileAccess.ReadWrite,
                    FileShare.Read
                );
                WritePcm16Header(_stream, format);
                PreexistingSamples = 0;
            }
        }

        public void WriteData(byte[] data, int count) => _stream.Write(data, 0, count);

        public void Flush()
        {
            lock (_stream)
            {
                _stream.Flush();
            }
        }

        /// <summary>Rewrites the RIFF/data sizes to match the current file length.</summary>
        public void PatchHeader()
        {
            lock (_stream)
            {
                var pos = _stream.Position;
                PatchWaveHeader(_stream);
                _stream.Position = pos;
            }
        }

        /// <summary>Number of PCM samples already in the file before this session (0 for fresh).</summary>
        public long PreexistingSamples { get; }

        public void Dispose()
        {
            _stream.Flush();
            _stream.Dispose();
        }

        /// <summary>Walks the RIFF chunk list to find the data chunk's start offset.</summary>
        private static long FindDataStart(Stream stream)
        {
            stream.Position = 12;
            var header = new byte[8];
            while (stream.Position + 8 <= stream.Length)
            {
                long chunkStart = stream.Position;
                stream.ReadExactly(header, 0, 8);
                uint tag = BitConverter.ToUInt32(header, 0);
                uint size = BitConverter.ToUInt32(header, 4);
                if (tag == 0x61746164u) // "data"
                    return chunkStart + 8;
                stream.Position = chunkStart + 8 + size + (size % 2);
            }
            throw new InvalidDataException("WAV has no data chunk.");
        }

        /// <summary>Writes a canonical 44-byte PCM16 header for the given format (sizes zeroed).</summary>
        private static void WritePcm16Header(Stream stream, WaveFormat format)
        {
            int blockAlign = format.Channels * (format.BitsPerSample / 8);
            using var w = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            w.Write("RIFF"u8);
            w.Write(0u); // RIFF size placeholder, patched later
            w.Write("WAVE"u8);
            w.Write("fmt "u8);
            w.Write(16);
            w.Write((short)1); // PCM
            w.Write((short)format.Channels);
            w.Write(format.SampleRate);
            w.Write(format.SampleRate * blockAlign); // byte rate
            w.Write((short)blockAlign);
            w.Write((short)format.BitsPerSample);
            w.Write("data"u8);
            w.Write(0u); // data size placeholder
        }
    }

    public void Dispose() => StopRecording();
}
