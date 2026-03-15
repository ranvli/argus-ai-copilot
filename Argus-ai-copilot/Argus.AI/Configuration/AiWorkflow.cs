namespace Argus.AI.Configuration;

/// <summary>
/// Named workflows within Argus that require AI capabilities.
/// Each workflow can be mapped to a different provider profile per capability.
/// </summary>
public enum AiWorkflow
{
    /// <summary>Live in-session assistant responding to spoken queries.</summary>
    RealtimeAssist,

    /// <summary>Semantic search and retrieval over indexed memories.</summary>
    MemoryQuery,

    /// <summary>Post-session summarisation, action-item extraction, and sentiment.</summary>
    MeetingSummary,

    /// <summary>Describe a captured screenshot or active window contents.</summary>
    ScreenExplain,

    /// <summary>
    /// Real-time speech-to-text transcription of captured audio.
    /// Intentionally separate from RealtimeAssist so transcription can use
    /// a different provider (e.g. local Whisper) while chat uses Ollama/OpenAI.
    /// </summary>
    SpeechTranscription
}
