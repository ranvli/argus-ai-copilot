using Argus.Audio.Capture;
using Argus.Audio.Devices;
using Microsoft.Extensions.DependencyInjection;

namespace Argus.Audio;

public static class AudioServiceExtensions
{
    /// <summary>
    /// Registers all Argus.Audio services.
    /// Call from Program.cs / ConfigureServices.
    /// </summary>
    public static IServiceCollection AddArgusAudio(this IServiceCollection services)
    {
        // Device discovery: singleton — enumerator is lightweight and stateless.
        services.AddSingleton<IAudioDeviceDiscovery, WindowsAudioDeviceDiscovery>();

        // Capture sources: transient — a fresh instance is created per session.
        services.AddTransient<MicrophoneCaptureSource>();
        services.AddTransient<SystemAudioCaptureSource>();

        return services;
    }
}
