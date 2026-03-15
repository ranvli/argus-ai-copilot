using Argus.Core.Domain.Entities;

namespace Argus.Transcription.Intent;

/// <summary>
/// Thread-safe rolling window of recent transcript segments.
/// Keeps the last <see cref="Capacity"/> segments so intent detection
/// always has fresh context without unbounded memory growth.
/// </summary>
public sealed class TranscriptBuffer
{
    private readonly Queue<TranscriptSegment> _segments;
    private readonly Lock _lock = new();

    /// <summary>Maximum number of segments retained.</summary>
    public int Capacity { get; }

    public TranscriptBuffer(int capacity = 30)
    {
        Capacity  = capacity;
        _segments = new Queue<TranscriptSegment>(capacity);
    }

    /// <summary>Appends new segments, evicting the oldest when over capacity.</summary>
    public void Push(IReadOnlyList<TranscriptSegment> incoming)
    {
        lock (_lock)
        {
            foreach (var seg in incoming)
            {
                if (_segments.Count >= Capacity)
                    _segments.Dequeue();
                _segments.Enqueue(seg);
            }
        }
    }

    /// <summary>Returns a snapshot of the current window as a single string.</summary>
    public string GetRecentText(int maxSegments = 10)
    {
        lock (_lock)
        {
            var take = _segments.TakeLast(maxSegments);
            return string.Join(" ", take.Select(s => s.Text.Trim()));
        }
    }

    /// <summary>Returns a snapshot of the most recent segments.</summary>
    public IReadOnlyList<TranscriptSegment> GetRecent(int maxSegments = 10)
    {
        lock (_lock)
        {
            return _segments.TakeLast(maxSegments).ToList();
        }
    }

    /// <summary>Clears the buffer (e.g. at session start).</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _segments.Clear();
        }
    }
}
