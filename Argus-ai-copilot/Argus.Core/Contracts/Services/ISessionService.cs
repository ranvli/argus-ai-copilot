using Argus.Core.Domain.Entities;
using Argus.Core.Domain.Enums;

namespace Argus.Core.Contracts.Services;

public interface ISessionService
{
    Task<Session> StartSessionAsync(string title, SessionType type, ListeningMode mode, CancellationToken ct = default);
    Task PauseSessionAsync(Guid sessionId, CancellationToken ct = default);
    Task ResumeSessionAsync(Guid sessionId, CancellationToken ct = default);
    Task EndSessionAsync(Guid sessionId, CancellationToken ct = default);
    Task<Session?> GetActiveSessionAsync(CancellationToken ct = default);
}
