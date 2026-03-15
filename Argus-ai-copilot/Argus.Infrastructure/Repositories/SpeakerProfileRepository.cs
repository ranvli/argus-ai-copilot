using Argus.Core.Contracts.Repositories;
using Argus.Core.Domain.Entities;
using Argus.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Argus.Infrastructure.Repositories;

internal sealed class SpeakerProfileRepository : ISpeakerProfileRepository
{
    private readonly ArgusDbContext _db;

    public SpeakerProfileRepository(ArgusDbContext db) => _db = db;

    public async Task<SpeakerProfile?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        await _db.SpeakerProfiles.FindAsync([id], ct);

    public async Task<IReadOnlyList<SpeakerProfile>> GetAllAsync(CancellationToken ct = default) =>
        await _db.SpeakerProfiles
            .OrderBy(p => p.DisplayName)
            .ToListAsync(ct);

    public async Task AddAsync(SpeakerProfile profile, CancellationToken ct = default)
    {
        _db.SpeakerProfiles.Add(profile);
        await _db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(SpeakerProfile profile, CancellationToken ct = default)
    {
        profile.UpdatedAt = DateTimeOffset.UtcNow;
        _db.SpeakerProfiles.Update(profile);
        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var profile = await _db.SpeakerProfiles.FindAsync([id], ct);
        if (profile is not null)
        {
            _db.SpeakerProfiles.Remove(profile);
            await _db.SaveChangesAsync(ct);
        }
    }
}
