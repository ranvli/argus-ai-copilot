using Argus.Core.Domain.Entities;

namespace Argus.Core.Contracts.Repositories;

public interface IScreenshotRepository
{
    Task<IReadOnlyList<ScreenshotArtifact>> GetBySessionAsync(Guid sessionId, CancellationToken ct = default);
    Task<ScreenshotArtifact?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task AddAsync(ScreenshotArtifact artifact, CancellationToken ct = default);
    Task DeleteBySessionAsync(Guid sessionId, CancellationToken ct = default);
}
