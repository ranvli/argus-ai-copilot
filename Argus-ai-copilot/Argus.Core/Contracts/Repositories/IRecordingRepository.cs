using Argus.Core.Domain.Entities;

namespace Argus.Core.Contracts.Repositories;

public interface IRecordingRepository
{
    Task<IReadOnlyList<RecordingArtifact>> GetBySessionAsync(Guid sessionId, CancellationToken ct = default);
    Task<RecordingArtifact?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task AddAsync(RecordingArtifact artifact, CancellationToken ct = default);
    Task DeleteBySessionAsync(Guid sessionId, CancellationToken ct = default);
}
