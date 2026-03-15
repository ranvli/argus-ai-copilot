using Argus.Core.Contracts.Repositories;
using Argus.Core.Domain.Entities;
using Argus.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Argus.Infrastructure.Repositories;

internal sealed class SessionRepository : ISessionRepository
{
    private readonly ArgusDbContext _db;

    public SessionRepository(ArgusDbContext db) => _db = db;

    public async Task<Session?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        await _db.Sessions
            .Include(s => s.Transcript)
            .Include(s => s.Screenshots)
            .Include(s => s.Recordings)
            .Include(s => s.Summary)
            .FirstOrDefaultAsync(s => s.Id == id, ct);

    public async Task<IReadOnlyList<Session>> GetAllAsync(CancellationToken ct = default) =>
        await _db.Sessions
            .OrderByDescending(s => s.StartedAt)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<Session>> GetRecentAsync(int count, CancellationToken ct = default) =>
        await _db.Sessions
            .OrderByDescending(s => s.StartedAt)
            .Take(count)
            .ToListAsync(ct);

    public async Task AddAsync(Session session, CancellationToken ct = default)
    {
        _db.Sessions.Add(session);
        await _db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(Session session, CancellationToken ct = default)
    {
        session.UpdatedAt = DateTimeOffset.UtcNow;
        _db.Sessions.Update(session);
        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var session = await _db.Sessions.FindAsync([id], ct);
        if (session is not null)
        {
            _db.Sessions.Remove(session);
            await _db.SaveChangesAsync(ct);
        }
    }
}
