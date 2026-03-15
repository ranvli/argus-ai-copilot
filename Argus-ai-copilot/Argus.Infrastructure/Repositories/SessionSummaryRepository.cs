using Argus.Core.Contracts.Repositories;
using Argus.Core.Domain.Entities;
using Argus.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Argus.Infrastructure.Repositories;

internal sealed class SessionSummaryRepository : ISessionSummaryRepository
{
    private readonly ArgusDbContext _db;

    public SessionSummaryRepository(ArgusDbContext db) => _db = db;

    public async Task<SessionSummary?> GetBySessionAsync(Guid sessionId, CancellationToken ct = default) =>
        await _db.SessionSummaries.FirstOrDefaultAsync(s => s.SessionId == sessionId, ct);

    public async Task AddAsync(SessionSummary summary, CancellationToken ct = default)
    {
        _db.SessionSummaries.Add(summary);
        await _db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(SessionSummary summary, CancellationToken ct = default)
    {
        _db.SessionSummaries.Update(summary);
        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteBySessionAsync(Guid sessionId, CancellationToken ct = default)
    {
        await _db.SessionSummaries
            .Where(s => s.SessionId == sessionId)
            .ExecuteDeleteAsync(ct);
    }
}
