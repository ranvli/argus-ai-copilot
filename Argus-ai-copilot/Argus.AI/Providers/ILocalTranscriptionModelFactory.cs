using Argus.AI.Configuration;

namespace Argus.AI.Providers;

/// <summary>
/// Factory bridge that lets <see cref="Resolvers.ModelResolver"/> (in Argus.AI) create
/// <c>WhisperLocalTranscriptionModel</c> instances (in Argus.Transcription) without
/// introducing a circular project reference.
///
/// Implementations live in Argus.Transcription and are registered in DI by
/// <c>TranscriptionServiceExtensions.AddArgusTranscription()</c>.
/// </summary>
public interface ILocalTranscriptionModelFactory
{
    /// <summary>
    /// Creates (or returns a cached) <see cref="ITranscriptionModel"/> for the given
    /// WhisperNet provider profile.
    /// </summary>
    ITranscriptionModel Create(ProviderProfile profile);

    bool CanCreate(ProviderProfile profile);
}
