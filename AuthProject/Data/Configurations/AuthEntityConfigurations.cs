using AuthProject.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuthProject.Data.Configurations;

internal static class Mapping
{
    internal const string BinaryCollation = "Latin1_General_100_BIN2";
    internal static void Entity<T>(EntityTypeBuilder<T> builder, string table) where T : EntityBase
    {
        builder.ToTable(table);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        if (typeof(VersionedEntity).IsAssignableFrom(typeof(T)))
            builder.Property<byte[]>(nameof(VersionedEntity.RowVersion)).IsRowVersion();
    }

    internal static void Digest(PropertyBuilder<string> builder) =>
        builder.HasMaxLength(64).IsUnicode(false).IsFixedLength().UseCollation(BinaryCollation);

    internal static void UserForeignKey<T>(EntityTypeBuilder<T> builder) where T : class =>
        builder.HasOne<User>().WithMany().HasForeignKey("UserId").OnDelete(DeleteBehavior.Restrict);
}

public sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> b)
    {
        Mapping.Entity(b, "Users");
        b.Property(x => x.Email).HasMaxLength(254);
        b.Property(x => x.NormalizedEmail).HasMaxLength(254).UseCollation(Mapping.BinaryCollation);
        b.Property(x => x.DisplayName).HasMaxLength(100);
        b.Property(x => x.PasswordHash).HasMaxLength(512);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(32).IsUnicode(false);
        b.HasIndex(x => x.NormalizedEmail).IsUnique();
        b.ToTable(t =>
        {
            t.HasCheckConstraint("CK_Users_Counters", "[AuthVersion] >= 1 AND [FailedLoginCount] >= 0");
            t.HasCheckConstraint("CK_Users_Email", "LEN([Email]) > 0 AND LEN([NormalizedEmail]) > 0");
            t.HasCheckConstraint("CK_Users_Status", "[Status] IN ('PendingVerification','Active','Disabled')");
        });
    }
}

public static class SystemRoles
{
    public static readonly Guid UserId = new("cb6a0875-a601-4371-8c71-3613fa83e745");
    public static readonly Guid AdminId = new("43c28b39-b52b-42eb-9c74-2f59caeaa428");
    public const string User = "User";
    public const string Admin = "Admin";
}

public sealed class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> b)
    {
        Mapping.Entity(b, "Roles");
        b.Property(x => x.Name).HasMaxLength(64);
        b.Property(x => x.NormalizedName).HasMaxLength(64).UseCollation(Mapping.BinaryCollation);
        b.HasIndex(x => x.NormalizedName).IsUnique();
        b.HasData(new Role { Id = SystemRoles.UserId, Name = SystemRoles.User, NormalizedName = "USER", IsSystem = true },
            new Role { Id = SystemRoles.AdminId, Name = SystemRoles.Admin, NormalizedName = "ADMIN", IsSystem = true });
    }
}

public sealed class UserRoleConfiguration : IEntityTypeConfiguration<UserRole>
{
    public void Configure(EntityTypeBuilder<UserRole> b)
    {
        b.ToTable("UserRoles");
        b.HasKey(x => new { x.UserId, x.RoleId });
        Mapping.UserForeignKey(b);
        b.HasOne<Role>().WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class SessionConfiguration : IEntityTypeConfiguration<AuthSession>
{
    public void Configure(EntityTypeBuilder<AuthSession> b)
    {
        Mapping.Entity(b, "AuthSessions");
        b.Property(x => x.PrimaryMethod).HasConversion<string>().HasMaxLength(16).IsUnicode(false).HasDefaultValue(AuthenticationMethod.Password);
        b.ToTable(t => t.HasCheckConstraint("CK_AuthSessions_PrimaryMethod", "[PrimaryMethod] IN ('Password', 'Google')"));
        Mapping.UserForeignKey(b);
        b.Property(x => x.RevokeReason).HasMaxLength(64).IsUnicode(false);
        b.Property(x => x.ClientInfo).HasMaxLength(256);
        b.HasIndex(x => new { x.UserId, x.RevokedAt });
        b.HasIndex(x => x.AbsoluteExpiresAt);
        b.ToTable(t => t.HasCheckConstraint("CK_AuthSessions_Lifetime",
            "[AuthVersion] >= 1 AND [AbsoluteExpiresAt] > [CreatedAt] AND [IdleExpiresAt] <= [AbsoluteExpiresAt] AND [IdleExpiresAt] > [CreatedAt]"));
    }
}

public sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> b)
    {
        Mapping.Entity(b, "RefreshTokens");
        Mapping.Digest(b.Property(x => x.TokenHash));
        b.HasOne<AuthSession>().WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<RefreshToken>().WithMany().HasForeignKey(x => x.ReplacedById).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.TokenHash).IsUnique();
        b.HasIndex(x => x.SessionId).IsUnique().HasFilter("[ConsumedAt] IS NULL AND [RevokedAt] IS NULL");
        b.HasIndex(x => new { x.SessionId, x.CreatedAt });
        b.HasIndex(x => x.ExpiresAt);
        b.ToTable(t =>
        {
            t.HasCheckConstraint("CK_RefreshTokens_Expiry", "[ExpiresAt] > [CreatedAt]");
            t.HasCheckConstraint("CK_RefreshTokens_Hash", "[TokenHash] NOT LIKE '%[^0-9A-F]%' AND LEN([TokenHash]) = 64");
        });
    }
}

public sealed class ChallengeConfiguration : IEntityTypeConfiguration<AuthChallenge>
{
    public void Configure(EntityTypeBuilder<AuthChallenge> b)
    {
        Mapping.Entity(b, "AuthChallenges");
        b.Property(x => x.PrimaryMethod).HasConversion<string>().HasMaxLength(16).IsUnicode(false).HasDefaultValue(AuthenticationMethod.Password);
        b.ToTable(t => t.HasCheckConstraint("CK_AuthChallenges_PrimaryMethod", "[PrimaryMethod] IN ('Password', 'Google')"));
        Mapping.UserForeignKey(b);
        Mapping.Digest(b.Property(x => x.SecretDigest));
        b.Property(x => x.KeyId).HasMaxLength(64).IsUnicode(false);
        b.Property(x => x.Purpose).HasConversion<string>().HasMaxLength(32).IsUnicode(false);
        b.HasIndex(x => new { x.UserId, x.Purpose, x.ContextId });
        b.HasIndex(x => x.ExpiresAt);
        b.ToTable(t =>
        {
            t.HasCheckConstraint("CK_AuthChallenges_Counters", "[Attempts] >= 0 AND [SendCount] >= 0 AND [Version] >= 1 AND [AuthVersion] >= 1");
            t.HasCheckConstraint("CK_AuthChallenges_Expiry", "[ExpiresAt] > [CreatedAt]");
            t.HasCheckConstraint("CK_AuthChallenges_Digest", "[SecretDigest] NOT LIKE '%[^0-9A-F]%' AND LEN([SecretDigest]) = 64");
        });
    }
}

public sealed class RecoveryCodeConfiguration : IEntityTypeConfiguration<RecoveryCode>
{
    public void Configure(EntityTypeBuilder<RecoveryCode> b)
    {
        Mapping.Entity(b, "RecoveryCodes");
        Mapping.UserForeignKey(b);
        Mapping.Digest(b.Property(x => x.CodeHash));
        b.HasIndex(x => x.CodeHash).IsUnique();
        b.HasIndex(x => new { x.UserId, x.UsedAt });
        b.ToTable(t => t.HasCheckConstraint("CK_RecoveryCodes_Hash", "[CodeHash] NOT LIKE '%[^0-9A-F]%' AND LEN([CodeHash]) = 64"));
    }
}

public sealed class ExternalLoginConfiguration : IEntityTypeConfiguration<ExternalLogin>
{
    public void Configure(EntityTypeBuilder<ExternalLogin> b)
    {
        Mapping.Entity(b, "ExternalLogins");
        Mapping.UserForeignKey(b);
        b.Property(x => x.Provider).HasMaxLength(32).IsUnicode(false).UseCollation(Mapping.BinaryCollation);
        b.Property(x => x.ProviderSubject).HasMaxLength(255).UseCollation(Mapping.BinaryCollation);
        b.HasIndex(x => new { x.Provider, x.ProviderSubject }).IsUnique();
        b.HasIndex(x => new { x.UserId, x.Provider }).IsUnique();
    }
}

public sealed class LoginHistoryConfiguration : IEntityTypeConfiguration<LoginHistory>
{
    public void Configure(EntityTypeBuilder<LoginHistory> b)
    {
        Mapping.Entity(b, "LoginHistories");
        Mapping.UserForeignKey(b);
        b.HasOne<AuthSession>().WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Restrict);
        b.Property(x => x.Method).HasConversion<string>().HasMaxLength(32).IsUnicode(false);
        b.Property(x => x.Outcome).HasConversion<string>().HasMaxLength(16).IsUnicode(false);
        b.Property(x => x.ReasonCode).HasConversion<string>().HasMaxLength(32).IsUnicode(false);
        b.Property(x => x.IpAddress).HasMaxLength(45).IsUnicode(false);
        b.Property(x => x.UserAgent).HasMaxLength(256);
        b.Property(x => x.TraceId).HasMaxLength(64).IsUnicode(false);
        b.HasIndex(x => new { x.UserId, x.OccurredAt });
    }
}

public sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> b)
    {
        Mapping.Entity(b, "AuditEvents");
        b.HasOne<User>().WithMany().HasForeignKey(x => x.ActorUserId).OnDelete(DeleteBehavior.Restrict);
        b.Property(x => x.EventType).HasConversion<string>().HasMaxLength(64).IsUnicode(false);
        b.Property(x => x.Outcome).HasConversion<string>().HasMaxLength(16).IsUnicode(false);
        b.Property(x => x.ReasonCode).HasConversion<string>().HasMaxLength(32).IsUnicode(false);
        b.Property(x => x.TraceId).HasMaxLength(64).IsUnicode(false);
        b.Property(x => x.SafeMetadata).HasMaxLength(1024);
        b.HasIndex(x => x.OccurredAt);
        b.HasIndex(x => new { x.ActorUserId, x.OccurredAt });
        b.HasIndex(x => new { x.EventType, x.OccurredAt });
        b.ToTable(t => t.HasCheckConstraint("CK_AuditEvents_Metadata", "ISJSON([SafeMetadata]) = 1"));
    }
}

public sealed class OutboxConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> b)
    {
        Mapping.Entity(b, "OutboxMessages");
        b.Property(x => x.Kind).HasConversion<string>().HasMaxLength(16).IsUnicode(false);
        b.Property(x => x.ProtectedPayload).HasMaxLength(32768);
        b.HasIndex(x => new { x.EventId, x.Kind }).IsUnique();
        b.HasIndex(x => x.NextAttemptAt).HasFilter("[ProcessedAt] IS NULL");
        b.ToTable(t => t.HasCheckConstraint("CK_OutboxMessages_Attempts", "[Attempts] >= 0"));
    }
}
