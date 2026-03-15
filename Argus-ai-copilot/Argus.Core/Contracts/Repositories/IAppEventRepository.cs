using Argus.Core.Domain.Entities;
using Argus.Core.Domain.Enums;

namespace Argus.Core.Contracts.Repositories;

public interface IAppEventRepository
{
    Task<IReadOnlyList<AppEvent>> GetRecentAsync(int count, CancellationToken ct = default);
    Task<IReadOnlyList<AppEvent>> GetBySessionAsync(Guid sessionId, CancellationToken ct = default);
    Task<IReadOnlyList<AppEvent>> GetByTypeAsync(AppEventType type, int count = 50, CancellationToken ct = default);
    Task AddAsync(AppEvent appEvent, CancellationToken ct = default);
    Task PruneOlderThanAsync(DateTimeOffset threshold, CancellationToken ct = default);
}
