using Argus.Core.Contracts.Repositories;
using Argus.Core.Domain.Entities;
using Argus.Core.Domain.Enums;
using Argus.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Argus.Infrastructure.Repositories;

internal sealed class AppEventRepository : IAppEventRepository
{
    private readonly ArgusDbContext _db;

    public AppEventRepository(ArgusDbContext db) => _db = db;

    public async Task<IReadOnlyList<AppEvent>> GetRecentAsync(int count, CancellationToken ct = default) =>
        await _db.AppEvents
            .OrderByDescending(e => e.OccurredAt)
            .Take(count)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<AppEvent>> GetBySessionAsync(Guid sessionId, CancellationToken ct = default) =>
        await _db.AppEvents
            .Where(e => e.SessionId == sessionId)
            .OrderBy(e => e.OccurredAt)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<AppEvent>> GetByTypeAsync(
        AppEventType type, int count = 50, CancellationToken ct = default) =>
        await _db.AppEvents
            .Where(e => e.Type == type)
            .OrderByDescending(e => e.OccurredAt)
            .Take(count)
            .ToListAsync(ct);

    public async Task AddAsync(AppEvent appEvent, CancellationToken ct = default)
    {
        _db.AppEvents.Add(appEvent);
        await _db.SaveChangesAsync(ct);
    }

    public async Task PruneOlderThanAsync(DateTimeOffset threshold, CancellationToken ct = default)
    {
        await _db.AppEvents
            .Where(e => e.OccurredAt < threshold)
            .ExecuteDeleteAsync(ct);
    }
}
