using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Argus.App.Diagnostics;

/// <summary>
/// Lightweight asynchronous UTF-8 file logger used for Phase 1 audio diagnostics.
/// Logging is queued off the capture/UI threads so verbose diagnostics do not stall WPF.
/// Files are capped and rotated per application run.
/// </summary>
internal sealed class DiagnosticFileLoggerProvider : ILoggerProvider
{
    private const long MaxFileBytes = 20L * 1024 * 1024;
    private const int MaxFilesPerRun = 4;

    private readonly ConcurrentDictionary<string, DiagnosticFileLogger> _loggers = new();
    private readonly Channel<string> _channel;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _writerTask;
    private readonly string _logDirectory;
    private readonly string _runStamp;
    private int _fileIndex;
    private StreamWriter? _writer;
    private long _bytesWritten;
    private volatile bool _disposed;

    public DiagnosticFileLoggerProvider(string logDirectory)
    {
        _logDirectory = logDirectory;
        _runStamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
        Directory.CreateDirectory(_logDirectory);

        _channel = Channel.CreateBounded<string>(new BoundedChannelOptions(8192)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });

        _writerTask = Task.Run(WriterLoopAsync);
    }

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, static (category, provider) => new DiagnosticFileLogger(provider, category), this);

    internal void Enqueue(string line)
    {
        if (_disposed) return;
        _channel.Writer.TryWrite(line);
    }

    private async Task WriterLoopAsync()
    {
        try
        {
            await foreach (var line in _channel.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                EnsureWriter(line);
                if (_writer is null) continue;

                await _writer.WriteLineAsync(line).ConfigureAwait(false);
                _bytesWritten += System.Text.Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // Diagnostics logging must never crash Argus.
        }
        finally
        {
            try
            {
                if (_writer is not null)
                {
                    await _writer.FlushAsync().ConfigureAwait(false);
                    _writer.Dispose();
                }
            }
            catch
            {
            }
        }
    }

    private void EnsureWriter(string nextLine)
    {
        var nextBytes = System.Text.Encoding.UTF8.GetByteCount(nextLine) + Environment.NewLine.Length;
        if (_writer is not null && _bytesWritten + nextBytes < MaxFileBytes)
            return;

        if (_writer is not null)
        {
            try
            {
                _writer.Flush();
                _writer.Dispose();
            }
            catch
            {
            }
            _writer = null;
        }

        if (_fileIndex >= MaxFilesPerRun)
            return;

        var suffix = _fileIndex == 0 ? string.Empty : $"-{_fileIndex}";
        var path = Path.Combine(_logDirectory, $"argus-{_runStamp}{suffix}.log");
        _fileIndex++;

        try
        {
            _writer = new StreamWriter(
                new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 64 * 1024, useAsync: true),
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = false
            };
            _bytesWritten = new FileInfo(path).Exists ? new FileInfo(path).Length : 0;
        }
        catch
        {
            _writer = null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _channel.Writer.TryComplete();
        try { _writerTask.Wait(TimeSpan.FromSeconds(2)); }
        catch { }

        _cts.Cancel();
        _cts.Dispose();
        _loggers.Clear();
    }

    private sealed class DiagnosticFileLogger : ILogger
    {
        private readonly DiagnosticFileLoggerProvider _provider;
        private readonly string _category;

        public DiagnosticFileLogger(DiagnosticFileLoggerProvider provider, string category)
        {
            _provider = provider;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var message = formatter(state, exception);
            var timestamp = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz");
            var threadId = Environment.CurrentManagedThreadId;
            var line = $"{timestamp} [{logLevel}] [{_category}] [T{threadId}] {message}";
            if (exception is not null)
                line += Environment.NewLine + exception;

            _provider.Enqueue(line);
        }
    }
}
