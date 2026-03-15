using Argus.AI.Configuration;

namespace Argus.AI.Providers;

/// <summary>
/// Resolves the correct provider implementation for a given workflow and capability.
/// </summary>
public interface IModelResolver
{
    /// <summary>Returns the chat model configured for this workflow.</summary>
    IChatModel ResolveChatModel(AiWorkflow workflow);

    /// <summary>Returns the vision model configured for this workflow.</summary>
    IVisionModel ResolveVisionModel(AiWorkflow workflow);

    /// <summary>Returns the transcription model configured for this workflow.</summary>
    ITranscriptionModel ResolveTranscriptionModel(AiWorkflow workflow);

    /// <summary>Returns the embedding model configured for this workflow.</summary>
    IEmbeddingModel ResolveEmbeddingModel(AiWorkflow workflow);

    /// <summary>Returns the TTS model configured for this workflow.</summary>
    ITtsModel ResolveTtsModel(AiWorkflow workflow);
}
