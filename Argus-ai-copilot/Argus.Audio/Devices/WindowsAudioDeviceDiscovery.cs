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

    /// <summary>
    /// Enumerates ALL active capture and render endpoints and logs them at Information level.
    /// Call once at startup to have a complete picture of the audio hardware in the log.
    /// Logs: endpoint ID, friendly name, device state, default role, mix format (channels/rate/bits),
    /// and WaveIn (WinMM) device capabilities including manufacturer/product IDs.
    /// </summary>
    public void LogAllEndpoints()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();

            // ── Default devices ───────────────────────────────────────────────
            foreach (var (flow, roleLabel) in new[]
            {
                (DataFlow.Capture, "Capture/Multimedia"),
                (DataFlow.Capture, "Capture/Communications"),
                (DataFlow.Render,  "Render/Multimedia"),
                (DataFlow.Render,  "Render/Communications"),
            })
            {
                var role = roleLabel.Contains("Communications") ? Role.Communications : Role.Multimedia;
                try
                {
                    var dev = enumerator.GetDefaultAudioEndpoint(flow, role);
                    _logger.LogInformation(
                        "[AudioEndpoints] DEFAULT {Role}: '{Name}'  Id={Id}  State={State}",
                        roleLabel, dev.FriendlyName, dev.ID, dev.State);
                }
                catch
                {
                    _logger.LogInformation("[AudioEndpoints] DEFAULT {Role}: (none)", roleLabel);
                }
            }

            // ── All active capture endpoints ──────────────────────────────────
            var captures = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
            _logger.LogInformation("[AudioEndpoints] Active capture endpoints: {Count}", captures.Count);
            for (int i = 0; i < captures.Count; i++)
            {
                var d = captures[i];
                string mixFmt = "(unavailable)";
                string muteState = "(unavailable)";
                string volume = "(unavailable)";
                try
                {
                    var fmt = d.AudioClient.MixFormat;
                    mixFmt = $"{fmt.SampleRate}Hz/{fmt.BitsPerSample}bit/{fmt.Channels}ch ({fmt.Encoding})";
                }
                catch { /* best-effort */ }
                try
                {
                    muteState = d.AudioEndpointVolume.Mute ? "MUTED" : "unmuted";
                    volume    = $"{d.AudioEndpointVolume.MasterVolumeLevelScalar:P0}";
                }
                catch { /* best-effort — some capture devices expose no volume control */ }

                _logger.LogInformation(
                    "[AudioEndpoints]   Capture[{Idx}] '{Name}'  Id={Id}  State={State}  " +
                    "MixFormat={MixFmt}  Mute={Mute}  Volume={Vol}",
                    i, d.FriendlyName, d.ID, d.State, mixFmt, muteState, volume);
            }

            // ── All active render endpoints ───────────────────────────────────
            var renders = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            _logger.LogInformation("[AudioEndpoints] Active render endpoints: {Count}", renders.Count);
            for (int i = 0; i < renders.Count; i++)
            {
                var d = renders[i];
                _logger.LogInformation(
                    "[AudioEndpoints]   Render[{Idx}] '{Name}'  Id={Id}",
                    i, d.FriendlyName, d.ID);
            }

            // ── WaveIn (WinMM MME) devices ────────────────────────────────────
            var waveInCount = NAudio.Wave.WaveIn.DeviceCount;
            _logger.LogInformation("[AudioEndpoints] WaveIn (WinMM) devices: {Count}", waveInCount);
            for (int i = 0; i < waveInCount; i++)
            {
                try
                {
                    var caps = NAudio.Wave.WaveIn.GetCapabilities(i);
                    _logger.LogInformation(
                        "[AudioEndpoints]   WaveIn[{Idx}] '{Name}'  Channels={Ch}  " +
                        "ManufacturerGuid={Mfr}  ProductGuid={Prod}",
                        i, caps.ProductName, caps.Channels,
                        caps.ManufacturerGuid, caps.ProductGuid);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[AudioEndpoints]   WaveIn[{Idx}] failed to query capabilities.", i);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[AudioEndpoints] Failed to enumerate audio endpoints.");
        }
    }
}
