namespace AuthProject.Entities;

public sealed class User : VersionedEntity
{
    public string Email { get; set; } = string.Empty;
    public string NormalizedEmail { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? PasswordHash { get; set; }
    public DateTimeOffset? EmailConfirmedAt { get; set; }
    public UserStatus Status { get; set; } = UserStatus.PendingVerification;
    public bool MfaEmailEnabled { get; set; }
    public long AuthVersion { get; set; } = 1;
    public int FailedLoginCount { get; set; }
    public DateTimeOffset? FailedLoginWindowStart { get; set; }
    public DateTimeOffset? LockoutEnd { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class Role : EntityBase
{
    public string Name { get; set; } = string.Empty;
    public string NormalizedName { get; set; } = string.Empty;
    public bool IsSystem { get; set; }
}

public sealed class UserRole
{
    public Guid UserId { get; set; }
    public Guid RoleId { get; set; }
}
