using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MeetingTranscriber.App.Services;

/// <summary>
/// Pulls two independent live capture streams (system loopback + microphone),
/// normalizes each to a common sample rate and channel count, mixes them into a
/// single interleaved IEEE-float stereo stream, and reports progress to a puller.
///
/// Per-source pipeline (each source is indepedently fed and resampled so its
/// internal filter state stays continuous):
///   BufferedWaveProvider (float, native rate/channels)
///     -> WaveToSampleProvider
///     -> WdlResamplingSampleProvider (target rate)
///     -> MonoToStereoSampleProvider (if the source was mono)
/// The mixer thread then reads a fixed number of frames from every source each
/// tick, sums them with per-source gain into the target buffer, and treats
/// shortfall as silence.
/// </summary>
public sealed class AudioMixer : IDisposable
{
    /// <summary>Only samples above this magnitude count as "signal" for diagnostics.</summary>
    private const float SignalThreshold = 0.005f;

    /// <summary>Role of a capture source, used to name its sidecar track file.</summary>
    public enum SourceRole
    {
        Loopback,
        Mic,
    }

    /// <summary>Identification for a single capture source added to the mixer.</summary>
    public sealed class Source
    {
        public required int Id { get; init; }
        public SourceRole Role { get; init; } = SourceRole.Loopback;
        public float Gain { get; set; } = 1.0f;
        public bool Active { get; set; } = true;
        public bool Complete { get; set; }
        internal BufferedWaveProvider? Buffer;
        internal ISampleProvider? Provider;

        // Second, independent pipeline feeding this source's sidecar track file (mono).
        internal BufferedWaveProvider? TrackBuffer;
        internal ISampleProvider? TrackProvider;

        /// <summary>Diagnostics (updated by the mix thread; read snapshots by the recorder).</summary>
        public float LastPeak; // max |sample| seen in the most recent read
        public long SignalSamples; // samples above SignalThreshold
        public long ArrivedSamples; // samples read in total
    }

    private readonly int _targetSampleRate;
    private readonly int _channels;
    private readonly object _sync = new();
    private readonly List<Source> _sources = new();

    public WaveFormat OutputWaveFormat { get; }

    public AudioMixer(int targetSampleRate = 48000, int channels = 2)
    {
        _targetSampleRate = targetSampleRate;
        _channels = channels;
        OutputWaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(targetSampleRate, channels);
    }

    /// <summary>
    /// Registers a capture source with the given native format. Returns a source id
    /// used by <see cref="Push"/>. The format should be IEEE-float; a best-effort
    /// conversion is attempted otherwise.
    /// </summary>
    public Source AddSource(
        WaveFormat sourceFormat,
        float gain = 1.0f,
        SourceRole role = SourceRole.Loopback
    )
    {
        lock (_sync)
        {
            // Normalize the inbound format to IEEE float at its native rate/channels.
            var floatFormat = WaveFormat.CreateIeeeFloatWaveFormat(
                sourceFormat.SampleRate,
                sourceFormat.Channels
            );

            var buffer = new BufferedWaveProvider(floatFormat)
            {
                BufferDuration = TimeSpan.FromSeconds(5),
                DiscardOnBufferOverflow = true,
            };
            var bufferRef = buffer;

            ISampleProvider pipeline = new WaveToSampleProvider(bufferRef);

            if (pipeline.WaveFormat.SampleRate != _targetSampleRate)
                pipeline = new WdlResamplingSampleProvider(pipeline, _targetSampleRate);

            if (pipeline.WaveFormat.Channels == 1 && _channels == 2)
                pipeline = new MonoToStereoSampleProvider(pipeline);
            else if (pipeline.WaveFormat.Channels != _channels)
                throw new InvalidOperationException(
                    $"Unsupported channel count {pipeline.WaveFormat.Channels}; expected 1 or {_channels}."
                );

            // Independent mono track pipeline (downmixed to 48k mono) for the sidecar file.
            var trackBuf = new BufferedWaveProvider(floatFormat)
            {
                BufferDuration = TimeSpan.FromSeconds(5),
                DiscardOnBufferOverflow = true,
            };
            ISampleProvider track = new WaveToSampleProvider(trackBuf);
            if (track.WaveFormat.SampleRate != _targetSampleRate)
                track = new WdlResamplingSampleProvider(track, _targetSampleRate);
            if (track.WaveFormat.Channels > 1)
                track = new StereoToMonoSampleProvider(track);

            var source = new Source
            {
                Id = _sources.Count,
                Role = role,
                Gain = gain,
                Buffer = bufferRef,
                Provider = pipeline,
                TrackBuffer = trackBuf,
                TrackProvider = track,
            };
            _sources.Add(source);
            return source;
        }
    }

    /// <summary>
    /// Pulls up to <paramref name="maxSamples"/> mono samples from a source's sidecar track
    /// pipeline. Returns the number of samples actually read.
    /// </summary>
    public int ReadTrack(Source source, float[] monoBuffer, int maxSamples)
    {
        lock (_sync)
        {
            if (!source.Active || source.TrackProvider is not { } provider)
                return 0;
            if (source.TrackBuffer is not { } buffer)
                return 0;

            int limit = Math.Min(maxSamples, monoBuffer.Length);
            if (limit <= 0)
                return 0;

            int read;
            lock (buffer)
            {
                read = provider.Read(monoBuffer, 0, limit);
            }
            return Math.Min(read, monoBuffer.Length);
        }
    }

    /// <summary>Marks a source complete so <see cref="IsDrained"/> can detect end-of-stream.</summary>
    public void CompleteSource(Source source)
    {
        lock (_sync)
        {
            source.Complete = true;
        }
    }

    /// <summary>
    /// Feeds raw captured bytes into a source's input buffer. Producer side.</summary>
    public void Push(int sourceId, byte[] data, int offset, int count)
    {
        Source source;
        lock (_sync)
        {
            if (sourceId < 0 || sourceId >= _sources.Count)
                return;
            source = _sources[sourceId];
        }

        var buffer = source.Buffer;
        if (buffer == null)
            return;
        lock (buffer)
        {
            buffer.AddSamples(data, offset, count);
        }

        // Duplicate the same bytes into the source's sidecar track buffer.
        if (source.TrackBuffer is { } trackBuf)
        {
            lock (trackBuf)
            {
                trackBuf.AddSamples(data, offset, count);
            }
        }
    }

    /// <summary>
    /// Pulls up to <paramref name="frameBuffer"/>'s capacity (or <paramref name="maxSamples"/>
    /// if given) of interleaved samples of mixed output. Returns the number of samples
    /// actually produced (0 when no source currently has buffered data). Completion is not
    /// decided here; the caller drains until it has stopped all sources and
    /// <see cref="IsDrained"/> is true.
    /// </summary>
    public int ReadMix(float[] frameBuffer, int maxSamples = 0)
    {
        lock (_sync)
        {
            var target = frameBuffer.Length;
            if (maxSamples > 0 && maxSamples < target)
                target = maxSamples;
            if (target <= 0)
                return 0;

            Array.Clear(frameBuffer, 0, target);

            var scratch = new float[target];
            int most = 0;

            foreach (var source in _sources)
            {
                if (!source.Active)
                    continue;

                var provider = source.Provider;
                if (provider == null)
                    continue;

                int read;
                var buffer = source.Buffer;
                if (buffer != null)
                {
                    lock (buffer)
                    {
                        read = provider.Read(scratch, 0, target);
                    }
                }
                else
                {
                    read = provider.Read(scratch, 0, target);
                }

                if (read > 0)
                {
                    float peak = 0f;
                    for (int i = 0; i < read; i++)
                    {
                        float v = scratch[i];
                        float abs = Math.Abs(v);
                        if (abs > peak)
                            peak = abs;
                        if (abs > SignalThreshold)
                            source.SignalSamples++;
                    }
                    source.ArrivedSamples += read;
                    source.LastPeak = peak;

                    for (int i = 0; i < read; i++)
                        frameBuffer[i] += scratch[i] * source.Gain;

                    if (read > most)
                        most = read;
                }
            }

            return most;
        }
    }

    /// <summary>
    /// True when every inactive/stopped source has fully drained its input buffer,
    /// i.e. there is no mixed audio left to pull.
    /// </summary>
    public bool IsDrained => _sources.All(s => !s.Active || (s.Complete && Empty(s.Buffer)));

    private static bool Empty(BufferedWaveProvider? buffer) =>
        buffer == null || buffer.BufferedBytes == 0;

    public void Dispose()
    {
        lock (_sync)
        {
            foreach (var source in _sources)
            {
                source.Buffer?.ClearBuffer();
            }
        }
    }
}
