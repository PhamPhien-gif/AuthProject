namespace AuthProject.Entities;

public sealed class AuthChallenge : VersionedEntity
{
    public Guid UserId { get; set; }
    public ChallengePurpose Purpose { get; set; }
    public AuthenticationMethod PrimaryMethod { get; set; } = AuthenticationMethod.Password;
    public string SecretDigest { get; set; } = string.Empty;
    public string? KeyId { get; set; }
    public Guid ContextId { get; set; }
    public int Version { get; set; } = 1;
    public long AuthVersion { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public int Attempts { get; set; }
    public int SendCount { get; set; }
    public DateTimeOffset? NextSendAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
}

public sealed class RecoveryCode : VersionedEntity
{
    public Guid UserId { get; set; }
    public string CodeHash { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
}

public sealed class ExternalLogin : EntityBase
{
    public Guid UserId { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string ProviderSubject { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}
