using Argus.Core.Domain.Entities;

namespace Argus.Core.Contracts.Repositories;

public interface ISettingsRepository
{
    Task<ProviderSettings?> GetByKeyAsync(string providerKey, CancellationToken ct = default);
    Task<IReadOnlyList<ProviderSettings>> GetAllAsync(CancellationToken ct = default);
    Task SaveAsync(ProviderSettings settings, CancellationToken ct = default);
    Task DeleteByKeyAsync(string providerKey, CancellationToken ct = default);
}
