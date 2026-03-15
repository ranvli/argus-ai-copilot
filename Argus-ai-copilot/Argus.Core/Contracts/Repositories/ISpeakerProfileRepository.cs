using Argus.Core.Domain.Entities;

namespace Argus.Core.Contracts.Repositories;

public interface ISpeakerProfileRepository
{
    Task<SpeakerProfile?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<SpeakerProfile>> GetAllAsync(CancellationToken ct = default);
    Task AddAsync(SpeakerProfile profile, CancellationToken ct = default);
    Task UpdateAsync(SpeakerProfile profile, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}
