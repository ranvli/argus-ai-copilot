using Argus.AI.Providers;
using Argus.Audio;
using Argus.Audio.Capture;
using Argus.Transcription.Pipeline;
using Argus.Transcription.Whisper;
using Microsoft.Extensions.DependencyInjection;

namespace Argus.Transcription;

public static class TranscriptionServiceExtensions
{
    /// <summary>
    /// Registers all Argus.Transcription services, including the local Whisper.net provider.
    /// Call from Program.cs / ConfigureServices.
    /// </summary>
    public static IServiceCollection AddArgusTranscription(this IServiceCollection services)
    {
        // Register audio capture sources first
        services.AddArgusAudio();

        // Whisper model service — singleton so the factory is created once and the
        // WhisperFactory instance (with its loaded model) is reused across calls.
        services.AddSingleton<WhisperModelService>();

        // Factory bridge — lets ModelResolver (in Argus.AI) create WhisperLocalTranscriptionModel
        // instances without taking a project dependency on Argus.Transcription.
        services.AddSingleton<ILocalTranscriptionModelFactory, WhisperTranscriptionModelFactory>();

        // Transient pipeline: a fresh instance per session keeps state isolated
        services.AddTransient<ITranscriptionPipeline, TranscriptionPipeline>();

        return services;
    }
}
