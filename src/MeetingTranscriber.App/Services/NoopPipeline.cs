namespace MeetingTranscriber.App.Services;

/// <summary>
/// Placeholder pipeline until the Python backend (Whisper transcribe +
/// configured-API summarizer) is wired up in later phases.
/// </summary>
public sealed class NoopPipeline : IConversationPipeline
{
    public Task<string> TranscribeAsync(
        string wavPath,
        IProgress<string>? progress = null,
        CancellationToken ct = default
    ) => Task.FromResult("(Transcription pipeline not wired up yet — coming in Phase 3.)");

    public Task<string> SummarizeAsync(
        string transcript,
        IProgress<string>? progress = null,
        CancellationToken ct = default
    ) => Task.FromResult("(Summarization pipeline not wired up yet — coming in Phase 4.)");
}
