using Argus.Core.Contracts.Repositories;
using Argus.Core.Domain.Entities;
using Argus.Core.Domain.ValueObjects;
using Argus.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Argus.Infrastructure.Repositories;

internal sealed class TranscriptRepository : ITranscriptRepository
{
    private readonly ArgusDbContext _db;

    public TranscriptRepository(ArgusDbContext db) => _db = db;

    public async Task<IReadOnlyList<TranscriptSegment>> GetBySessionAsync(Guid sessionId, CancellationToken ct = default) =>
        await _db.TranscriptSegments
            .Where(t => t.SessionId == sessionId)
            .OrderBy(t => t.CreatedAt)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<TranscriptSegment>> GetBySessionAndRangeAsync(
        Guid sessionId, TimeRange range, CancellationToken ct = default) =>
        await _db.TranscriptSegments
            .Where(t => t.SessionId == sessionId
                && t.Range.Start >= range.Start
                && t.Range.End <= range.End)
            .OrderBy(t => t.CreatedAt)
            .ToListAsync(ct);

    public async Task AddAsync(TranscriptSegment segment, CancellationToken ct = default)
    {
        _db.TranscriptSegments.Add(segment);
        await _db.SaveChangesAsync(ct);
    }

    public async Task AddRangeAsync(IEnumerable<TranscriptSegment> segments, CancellationToken ct = default)
    {
        _db.TranscriptSegments.AddRange(segments);
        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteBySessionAsync(Guid sessionId, CancellationToken ct = default)
    {
        await _db.TranscriptSegments
            .Where(t => t.SessionId == sessionId)
            .ExecuteDeleteAsync(ct);
    }
}
