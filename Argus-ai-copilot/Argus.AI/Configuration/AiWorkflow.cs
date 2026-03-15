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
    ScreenExplain
}
