namespace Argus.Transcription.Intent;

/// <summary>Coarse intent categories recognised from spoken text.</summary>
public enum DetectedIntent
{
    /// <summary>No actionable intent found.</summary>
    None,

    /// <summary>User spoke a wake phrase directed at Argus.</summary>
    WakeWord,

    /// <summary>User is asking for a suggested reply to something they heard.</summary>
    SuggestReply,

    /// <summary>User is asking what to say or how to respond.</summary>
    HowToRespond,

    /// <summary>User wants Argus to look at / explain the current context.</summary>
    ExplainContext,

    /// <summary>General help or assistance request.</summary>
    GeneralHelp,
}

/// <summary>Result of a single intent detection pass over recent transcript text.</summary>
public sealed class IntentDetectionResult
{
    public static readonly IntentDetectionResult None =
        new(DetectedIntent.None, string.Empty, string.Empty);

    public DetectedIntent Intent       { get; }
    public string         WakePhrase   { get; }
    public string         ContextText  { get; }
    public bool           HasIntent    => Intent != DetectedIntent.None;

    public IntentDetectionResult(DetectedIntent intent, string wakePhrase, string contextText)
    {
        Intent      = intent;
        WakePhrase  = wakePhrase;
        ContextText = contextText;
    }
}
