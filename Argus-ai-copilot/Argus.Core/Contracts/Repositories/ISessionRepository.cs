using Argus.Core.Domain.Entities;

namespace Argus.Core.Contracts.Repositories;

public interface ISessionRepository
{
    Task<Session?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<Session>> GetAllAsync(CancellationToken ct = default);
    Task<IReadOnlyList<Session>> GetRecentAsync(int count, CancellationToken ct = default);
    Task AddAsync(Session session, CancellationToken ct = default);
    Task UpdateAsync(Session session, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}
