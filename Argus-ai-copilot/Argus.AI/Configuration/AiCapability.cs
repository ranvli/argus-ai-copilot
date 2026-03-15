namespace Argus.AI.Configuration;

/// <summary>
/// The discrete AI capability being requested.
/// Each capability maps to one provider interface.
/// </summary>
public enum AiCapability
{
    Chat,
    Vision,
    Transcription,
    Embeddings,
    Tts
}
