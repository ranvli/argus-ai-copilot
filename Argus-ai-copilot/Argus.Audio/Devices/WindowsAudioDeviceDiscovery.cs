using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;

namespace Argus.Audio.Devices;

/// <summary>
/// Enumerates Windows WASAPI audio endpoint devices using NAudio's
/// <see cref="MMDeviceEnumerator"/>.
///
/// All methods create a short-lived <see cref="MMDeviceEnumerator"/> so the
/// snapshot reflects the devices that are active at the moment of the call.
/// </summary>
public sealed class WindowsAudioDeviceDiscovery : IAudioDeviceDiscovery
{
    private readonly ILogger<WindowsAudioDeviceDiscovery> _logger;

    public WindowsAudioDeviceDiscovery(ILogger<WindowsAudioDeviceDiscovery> logger)
        => _logger = logger;

    // ── IAudioDeviceDiscovery ─────────────────────────────────────────────────

    public IReadOnlyList<AudioDeviceInfo> GetInputDevices()
        => Enumerate(DataFlow.Capture);

    public IReadOnlyList<AudioDeviceInfo> GetOutputDevices()
        => Enumerate(DataFlow.Render);

    public AudioDeviceInfo? GetDefaultInputDevice()
        => GetDefault(DataFlow.Capture, Role.Multimedia);

    public AudioDeviceInfo? GetDefaultOutputDevice()
        => GetDefault(DataFlow.Render, Role.Multimedia);

    // ── Helpers ───────────────────────────────────────────────────────────────

    private IReadOnlyList<AudioDeviceInfo> Enumerate(DataFlow flow)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var collection       = enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active);
            var kind             = flow == DataFlow.Capture ? AudioDeviceKind.Input : AudioDeviceKind.Output;

            // Identify the default device so we can set IsDefault = true on it.
            string? defaultId = null;
            try
            {
                var role = Role.Multimedia;
                defaultId = enumerator.GetDefaultAudioEndpoint(flow, role).ID;
            }
            catch { /* no default — leave null */ }

            var result = new List<AudioDeviceInfo>(collection.Count);
            for (int i = 0; i < collection.Count; i++)
            {
                var dev = collection[i];
                result.Add(new AudioDeviceInfo
                {
                    Id          = dev.ID,
                    Name        = dev.FriendlyName,
                    Kind        = kind,
                    IsDefault   = dev.ID == defaultId,
                    IsAvailable = true
                });
            }

            _logger.LogDebug(
                "AudioDeviceDiscovery: found {Count} active {Flow} device(s). Default={Default}",
                result.Count, flow, result.FirstOrDefault(d => d.IsDefault)?.Name ?? "none");

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AudioDeviceDiscovery: failed to enumerate {Flow} devices.", flow);
            return [];
        }
    }

    private AudioDeviceInfo? GetDefault(DataFlow flow, Role role)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var dev              = enumerator.GetDefaultAudioEndpoint(flow, role);
            var kind             = flow == DataFlow.Capture ? AudioDeviceKind.Input : AudioDeviceKind.Output;

            var info = new AudioDeviceInfo
            {
                Id          = dev.ID,
                Name        = dev.FriendlyName,
                Kind        = kind,
                IsDefault   = true,
                IsAvailable = true
            };

            _logger.LogInformation(
                "AudioDeviceDiscovery: default {Kind} device = '{Name}'  Id={Id}",
                kind, info.Name, info.Id);

            return info;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex, "AudioDeviceDiscovery: no default {Flow} device found.", flow);
            return null;
        }
    }
}
