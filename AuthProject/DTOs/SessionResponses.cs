namespace AuthProject.DTOs;

public sealed record CsrfResponse(string RequestToken);
public sealed record SessionResponse(Guid Id, DateTimeOffset CreatedAt, DateTimeOffset LastSeenAt,
    DateTimeOffset IdleExpiresAt, DateTimeOffset AbsoluteExpiresAt, DateTimeOffset? RevokedAt, bool IsCurrent);
public sealed record PagedResponse<T>(T[] Items, int Page, int PageSize, int TotalCount);
public sealed class RevokeSessionRequest
{
    public Guid SessionId { get; init; }
}
