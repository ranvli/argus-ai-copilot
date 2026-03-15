using Argus.Core.Contracts.Repositories;
using Argus.Core.Domain.Entities;
using Argus.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Argus.Infrastructure.Repositories;

internal sealed class SettingsRepository : ISettingsRepository
{
    private readonly ArgusDbContext _db;

    public SettingsRepository(ArgusDbContext db) => _db = db;

    public async Task<ProviderSettings?> GetByKeyAsync(string providerKey, CancellationToken ct = default) =>
        await _db.ProviderSettings.FirstOrDefaultAsync(p => p.ProviderKey == providerKey, ct);

    public async Task<IReadOnlyList<ProviderSettings>> GetAllAsync(CancellationToken ct = default) =>
        await _db.ProviderSettings.OrderBy(p => p.ProviderKey).ToListAsync(ct);

    public async Task SaveAsync(ProviderSettings settings, CancellationToken ct = default)
    {
        var existing = await _db.ProviderSettings
            .FirstOrDefaultAsync(p => p.ProviderKey == settings.ProviderKey, ct);

        if (existing is null)
            _db.ProviderSettings.Add(settings);
        else
        {
            existing.ApiKey = settings.ApiKey;
            existing.Endpoint = settings.Endpoint;
            existing.ModelId = settings.ModelId;
            existing.ExtraProperties = settings.ExtraProperties;
            existing.IsEnabled = settings.IsEnabled;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteByKeyAsync(string providerKey, CancellationToken ct = default)
    {
        await _db.ProviderSettings
            .Where(p => p.ProviderKey == providerKey)
            .ExecuteDeleteAsync(ct);
    }
}
