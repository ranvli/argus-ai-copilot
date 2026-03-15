using Argus.Audio.Capture;
using Argus.Infrastructure.Storage;
using Microsoft.Extensions.Logging;
using WhisperFactory = Whisper.net.WhisperFactory;
using WhisperGgmlDownloader = Whisper.net.Ggml.WhisperGgmlDownloader;
using GgmlType = Whisper.net.Ggml.GgmlType;
using QuantizationType = Whisper.net.Ggml.QuantizationType;

namespace Argus.Transcription.Whisper;

/// <summary>
/// Singleton service responsible for:
/// <list type="bullet">
///   <item>Resolving the on-disk path for a given Whisper GGML model.</item>
///   <item>Downloading the model file via <c>WhisperGgmlDownloader</c> when it is absent.</item>
///   <item>Creating and caching a <c>WhisperFactory</c> for subsequent use.</item>
/// </list>
///
/// All I/O is guarded by an async semaphore so concurrent callers during startup
/// only trigger one download, not N simultaneous downloads of the same file.
/// </summary>
public sealed class WhisperModelService : IAsyncDisposable
{
    private readonly IPathProvider _pathProvider;
    private readonly ILogger<WhisperModelService> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private WhisperFactory? _factory;

    public WhisperModelDownloadState DownloadState { get; private set; } =
        WhisperModelDownloadState.NotChecked;

    /// <summary>Full path to the model file (populated after first EnsureModelAsync call).</summary>
    public string ModelPath { get; private set; } = string.Empty;

    public WhisperModelService(IPathProvider pathProvider, ILogger<WhisperModelService> logger)
    {
        _pathProvider = pathProvider;
        _logger       = logger;
    }

    /// <summary>
    /// Ensures the GGML model file for <paramref name="modelId"/> (e.g. "base.en") is present
    /// on disk, downloading it if necessary, then returns a ready <c>WhisperFactory</c>.
    /// Thread-safe: concurrent callers block until the first call completes.
    /// </summary>
    public async Task<WhisperFactory> EnsureModelAsync(string modelId, CancellationToken ct = default)
    {
        if (_factory is not null)
            return _factory;

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Double-check after acquiring the lock
            if (_factory is not null)
                return _factory;

            var modelsDir = _pathProvider.WhisperModelsFolder;
            Directory.CreateDirectory(modelsDir);

            var path = Path.Combine(modelsDir, $"ggml-{modelId}.bin");
            ModelPath = path;

            if (!File.Exists(path))
            {
                var ggmlType = ParseGgmlType(modelId);
                _logger.LogInformation(
                    "[WhisperModel] Model file not found at '{Path}'. Downloading {Type}…",
                    path, ggmlType);

                DownloadState = WhisperModelDownloadState.Downloading;

                try
                {
                    await using var modelStream =
                        await WhisperGgmlDownloader.Default
                            .GetGgmlModelAsync(ggmlType, QuantizationType.NoQuantization, ct)
                            .ConfigureAwait(false);

                    await using var fileStream =
                        new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None,
                            bufferSize: 81920, useAsync: true);

                    await modelStream.CopyToAsync(fileStream, ct).ConfigureAwait(false);

                    _logger.LogInformation(
                        "[WhisperModel] Download complete. ModelId={ModelId} Path='{Path}'",
                        modelId, path);
                }
                catch (Exception ex)
                {
                    DownloadState = WhisperModelDownloadState.Failed;
                    // Clean up partial file so the next attempt re-downloads cleanly
                    if (File.Exists(path))
                    {
                        try { File.Delete(path); } catch { /* best-effort */ }
                    }
                    _logger.LogError(ex,
                        "[WhisperModel] Download failed for ModelId={ModelId}. Path='{Path}'",
                        modelId, path);
                    throw;
                }
            }
            else
            {
                _logger.LogInformation(
                    "[WhisperModel] Model file found at '{Path}'.", path);
            }

            _logger.LogInformation(
                "[WhisperModel] Initialising WhisperFactory. ModelId={ModelId}", modelId);

            _factory = WhisperFactory.FromPath(path);
            DownloadState = WhisperModelDownloadState.Ready;

            _logger.LogInformation(
                "[WhisperModel] WhisperFactory ready. ModelId={ModelId}", modelId);

            return _factory;
        }
        finally
        {
            _lock.Release();
        }
    }

    // ── IAsyncDisposable ──────────────────────────────────────────────────────

    public ValueTask DisposeAsync()
    {
        _lock.Dispose();
        _factory?.Dispose();
        _factory = null;
        return ValueTask.CompletedTask;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Maps a human-readable model ID string to the <c>GgmlType</c> enum.
    /// Supported: tiny, tiny.en, base, base.en, small, small.en, medium, medium.en, large-v1/v2/v3.
    /// Falls back to <c>GgmlType.BaseEn</c> if unrecognised.
    /// </summary>
    private GgmlType ParseGgmlType(string modelId)
    {
        return modelId.ToLowerInvariant().Replace("-", ".") switch
        {
            "tiny"      => GgmlType.Tiny,
            "tiny.en"   => GgmlType.TinyEn,
            "base"      => GgmlType.Base,
            "base.en"   => GgmlType.BaseEn,
            "small"     => GgmlType.Small,
            "small.en"  => GgmlType.SmallEn,
            "medium"    => GgmlType.Medium,
            "medium.en" => GgmlType.MediumEn,
            "large"     => GgmlType.LargeV1,
            "large.v1"  => GgmlType.LargeV1,
            "large.v2"  => GgmlType.LargeV2,
            "large.v3"  => GgmlType.LargeV3,
            _           => LogFallback(modelId, GgmlType.BaseEn)
        };
    }

    private GgmlType LogFallback(string modelId, GgmlType fallback)
    {
        _logger.LogWarning(
            "[WhisperModel] Unrecognised ModelId='{ModelId}'. Falling back to {Fallback}.",
            modelId, fallback);
        return fallback;
    }
}
