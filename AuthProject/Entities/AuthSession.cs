namespace AuthProject.Entities;

public sealed class AuthSession : VersionedEntity
{
    public Guid UserId { get; set; }
    public long AuthVersion { get; set; }
    public AuthenticationMethod PrimaryMethod { get; set; } = AuthenticationMethod.Password;
    public DateTimeOffset AuthenticatedAt { get; set; }
    public DateTimeOffset? MfaVerifiedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public DateTimeOffset IdleExpiresAt { get; set; }
    public DateTimeOffset AbsoluteExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? RevokeReason { get; set; }
    public string? ClientInfo { get; set; }
}

public sealed class RefreshToken : VersionedEntity
{
    public Guid SessionId { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public Guid? ReplacedById { get; set; }
}
