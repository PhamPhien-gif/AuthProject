using AuthProject.DTOs;

namespace AuthProject.Repositories.Interfaces;

public interface ISessionStore
{
    Task<RefreshAttempt> RefreshAsync(string digest, string trace, CancellationToken ct);
    Task LogoutAsync(string digest, string trace, CancellationToken ct);
    Task LogoutAllAsync(Guid actor, string trace, CancellationToken ct);
    Task<bool> RevokeOwnedAsync(Guid actor, Guid id, string trace, CancellationToken ct);
    Task<PagedResponse<SessionResponse>> ListAsync(Guid actor, Guid current, PagingRequest paging, CancellationToken ct);
}
