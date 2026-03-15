using Argus.Core.Domain.Entities;

namespace Argus.Core.Contracts.Repositories;

public interface ISessionSummaryRepository
{
    Task<SessionSummary?> GetBySessionAsync(Guid sessionId, CancellationToken ct = default);
    Task AddAsync(SessionSummary summary, CancellationToken ct = default);
    Task UpdateAsync(SessionSummary summary, CancellationToken ct = default);
    Task DeleteBySessionAsync(Guid sessionId, CancellationToken ct = default);
}
