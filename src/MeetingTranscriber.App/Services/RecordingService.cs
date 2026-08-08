using System.Diagnostics;
using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MeetingTranscriber.App.Services;

/// <summary>Result of a completed recording session.</summary>
public sealed record RecordingResult(string Path, float DurationSeconds);

/// <summary>
/// Captures system audio (WASAPI loopback = "speaker out") and an optional
/// microphone simultaneously, mixes both into a single 48 kHz stereo stream via
/// <see cref="AudioMixer"/>, and writes it to a WAV file on a background thread.
/// </summary>
public sealed class RecordingService : IDisposable
{
    private static readonly WaveFormat OutputFormat = new(48000, 16, 2);

    private readonly object _sync = new();
    private AudioMixer? _mixer;
    private WaveFileWriter? _writer;
    private Thread? _mixThread;
    private WasapiLoopbackCapture? _loopback;
    private WasapiCapture? _mic;
    private AudioMixer.Source? _loopbackSource;
    private AudioMixer.Source? _micSource;
    private volatile bool _running;
    private volatile bool _stopRequested;
    private long _lastNonZeroSample = -1;
    private string _outputPath = string.Empty;

    public bool IsRecording => _running;

    /// <summary>Raised (on the UI thread marshaler) when recording ends with the result.</summary>
    public event Action<RecordingResult>? RecordingStopped;

    public void StartRecording(
        MMDevice loopbackDevice,
        MMDevice? micDevice,
        float micGain,
        string outputDir
    )
    {
        lock (_sync)
        {
            if (_running)
                return;

            Directory.CreateDirectory(outputDir);
            _outputPath = System.IO.Path.Combine(
                outputDir,
                DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".wav"
            );

            _mixer = new AudioMixer(48000, 2);

            // --- System audio (loopback) ---
            _loopback = new WasapiLoopbackCapture(loopbackDevice);
            var loopbackFormat = _loopback.WaveFormat;
            _loopbackSource = _mixer.AddSource(loopbackFormat, gain: 0.85f);
            var loopbackSourceRef = _loopbackSource;
            _loopback.DataAvailable += (_, e) =>
            {
                var floats = NormalizeToFloat(loopbackFormat, e.Buffer, e.BytesRecorded);
                _mixer.Push(loopbackSourceRef.Id, floats, 0, floats.Length);
            };
            _loopback.RecordingStopped += (_, _) => _mixer.CompleteSource(loopbackSourceRef);

            // --- Microphone (optional) ---
            if (micDevice != null)
            {
                _mic = new WasapiCapture(micDevice);
                var micFormat = _mic.WaveFormat;
                _micSource = _mixer.AddSource(micFormat, gain: Math.Clamp(micGain, 0f, 4f));
                var micSourceRef = _micSource;
                _mic.DataAvailable += (_, e) =>
                {
                    var floats = NormalizeToFloat(micFormat, e.Buffer, e.BytesRecorded);
                    _mixer.Push(micSourceRef.Id, floats, 0, floats.Length);
                };
                _mic.RecordingStopped += (_, _) => _mixer.CompleteSource(micSourceRef);
            }

            _writer = new WaveFileWriter(_outputPath, OutputFormat);
            var chunk = new float[9600]; // 100 ms of 48 kHz stereo

            _mixThread = new Thread(() => MixLoop(chunk))
            {
                IsBackground = true,
                Name = "MeetingMixer",
            };

            _running = true;
            _stopRequested = false;
            _loopback.StartRecording();
            _mic?.StartRecording();
            _mixThread.Start();
        }
    }

    public RecordingResult? StopRecording()
    {
        lock (_sync)
        {
            if (!_running)
                return null;

            _stopRequested = true;

            // Stop captures; also explicitly complete sources so IsDrained resolves
            // even if a RecordingStopped callback never fired.
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
            if (_loopbackSource != null)
                _mixer?.CompleteSource(_loopbackSource);
            if (_micSource != null)
                _mixer?.CompleteSource(_micSource);

            _mixThread?.Join(TimeSpan.FromSeconds(15));
            _mixThread = null;

            var path = _outputPath;
            var duration = 0f;
            if (_writer != null)
            {
                duration =
                    _writer.Length
                    / (float)(
                        OutputFormat.SampleRate
                        * OutputFormat.Channels
                        * (OutputFormat.BitsPerSample / 8)
                    );
                _writer.Dispose();
                _writer = null;
            }

            // Trim the trailing silence the resampler padded the recording with.
            if (_lastNonZeroSample >= 0)
                TrimWaveFile(path, (_lastNonZeroSample + 1) * 2);

            _loopback?.Dispose();
            _loopback = null;
            _mic?.Dispose();
            _mic = null;
            _mixer?.Dispose();
            _mixer = null;
            _running = false;

            var result = new RecordingResult(path, duration);
            RecordingStopped?.Invoke(result);
            return result;
        }
    }

    private void MixLoop(float[] chunk)
    {
        var shortBuf = new short[chunk.Length];
        var byteBuf = new byte[chunk.Length * 2];
        int drainTicks = 0;
        long totalSamples = 0;
        _lastNonZeroSample = -1; // last non-zero sample index, for trailing-silence trim

        while (true)
        {
            var count = _mixer!.ReadMix(chunk);
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
                    _writer!.Write(byteBuf, 0, count * 2);
                    totalSamples += count;
                }
            }

            // The resampler streams an endless tail of zeros once its input drains, so
            // ReadMix never returns 0. Once stopping is requested and every input buffer
            // is empty, keep pulling for a short bounded window to flush the resampler's
            // real signal tail, then finish and trim the trailing zeros that padded it.
            if (_stopRequested && _mixer.IsDrained)
            {
                if (++drainTicks >= 12) // ~1.2 s of pull window (trimmed from output)
                    break;
            }
            else
            {
                drainTicks = 0;
            }
            Thread.Sleep(10);
        }

        if (_writer != null)
            _writer.Flush();
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

    public void Dispose() => StopRecording();
}
