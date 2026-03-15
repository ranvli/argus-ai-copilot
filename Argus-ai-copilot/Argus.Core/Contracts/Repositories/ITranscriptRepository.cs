using Argus.Core.Domain.Entities;
using Argus.Core.Domain.ValueObjects;

namespace Argus.Core.Contracts.Repositories;

public interface ITranscriptRepository
{
    Task<IReadOnlyList<TranscriptSegment>> GetBySessionAsync(Guid sessionId, CancellationToken ct = default);
    Task<IReadOnlyList<TranscriptSegment>> GetBySessionAndRangeAsync(Guid sessionId, TimeRange range, CancellationToken ct = default);
    Task AddAsync(TranscriptSegment segment, CancellationToken ct = default);
    Task AddRangeAsync(IEnumerable<TranscriptSegment> segments, CancellationToken ct = default);
    Task DeleteBySessionAsync(Guid sessionId, CancellationToken ct = default);
}
