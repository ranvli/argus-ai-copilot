using Argus.Core.Contracts.Repositories;
using Argus.Core.Domain.Entities;
using Argus.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Argus.Infrastructure.Repositories;

internal sealed class ScreenshotRepository : IScreenshotRepository
{
    private readonly ArgusDbContext _db;

    public ScreenshotRepository(ArgusDbContext db) => _db = db;

    public async Task<IReadOnlyList<ScreenshotArtifact>> GetBySessionAsync(Guid sessionId, CancellationToken ct = default) =>
        await _db.Screenshots
            .Where(s => s.SessionId == sessionId)
            .OrderBy(s => s.CapturedAt)
            .ToListAsync(ct);

    public async Task<ScreenshotArtifact?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        await _db.Screenshots.FindAsync([id], ct);

    public async Task AddAsync(ScreenshotArtifact artifact, CancellationToken ct = default)
    {
        _db.Screenshots.Add(artifact);
        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteBySessionAsync(Guid sessionId, CancellationToken ct = default)
    {
        await _db.Screenshots
            .Where(s => s.SessionId == sessionId)
            .ExecuteDeleteAsync(ct);
    }
}
