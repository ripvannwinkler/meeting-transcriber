namespace MeetingTranscriber.App.Services;

/// <summary>
/// Turns a recorded WAV into a transcript, then a summary. Implemented by the
/// Python-backed pipeline (Whisper + OpenAI-compatible summarizer); a no-op
/// placeholder is used until that lands.
/// </summary>
public interface IConversationPipeline
{
    /// <summary>
    /// Transcribes a WAV (optionally with separate loopback/mic tracks for dual-stream
    /// transcription) to text, reporting progress as it goes.
    /// </summary>
    Task<string> TranscribeAsync(
        string wavPath,
        IProgress<string>? progress = null,
        CancellationToken ct = default,
        string? loopbackTrack = null,
        string? micTrack = null
    );

    /// <summary>Summarizes a transcript into structured notes (key points, decisions, actions).</summary>
    Task<string> SummarizeAsync(
        string transcript,
        IProgress<string>? progress = null,
        CancellationToken ct = default
    );
}
