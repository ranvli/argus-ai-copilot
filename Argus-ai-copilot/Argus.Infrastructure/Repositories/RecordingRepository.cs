using Argus.Core.Contracts.Repositories;
using Argus.Core.Domain.Entities;
using Argus.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Argus.Infrastructure.Repositories;

internal sealed class RecordingRepository : IRecordingRepository
{
    private readonly ArgusDbContext _db;

    public RecordingRepository(ArgusDbContext db) => _db = db;

    public async Task<IReadOnlyList<RecordingArtifact>> GetBySessionAsync(Guid sessionId, CancellationToken ct = default) =>
        await _db.Recordings
            .Where(r => r.SessionId == sessionId)
            .OrderBy(r => r.CreatedAt)
            .ToListAsync(ct);

    public async Task<RecordingArtifact?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        await _db.Recordings.FindAsync([id], ct);

    public async Task AddAsync(RecordingArtifact artifact, CancellationToken ct = default)
    {
        _db.Recordings.Add(artifact);
        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteBySessionAsync(Guid sessionId, CancellationToken ct = default)
    {
        await _db.Recordings
            .Where(r => r.SessionId == sessionId)
            .ExecuteDeleteAsync(ct);
    }
}
