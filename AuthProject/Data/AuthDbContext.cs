using AuthProject.Entities;
using AuthProject.Security;
using Microsoft.EntityFrameworkCore;

namespace AuthProject.Data;

public sealed class AuthDbContext(DbContextOptions<AuthDbContext> options, TimeProvider? clock = null) : DbContext(options)
{
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    public DbSet<User> Users => Set<User>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<AuthSession> Sessions => Set<AuthSession>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<AuthChallenge> Challenges => Set<AuthChallenge>();
    public DbSet<RecoveryCode> RecoveryCodes => Set<RecoveryCode>();
    public DbSet<ExternalLogin> ExternalLogins => Set<ExternalLogin>();
    public DbSet<LoginHistory> LoginHistories => Set<LoginHistory>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AuthDbContext).Assembly);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        PrepareChanges();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        PrepareChanges();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void PrepareChanges()
    {
        ChangeTracker.DetectChanges();
        if (ChangeTracker.Entries().Any(e => e.Entity is AuditEvent or LoginHistory
            && e.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Audit and login history are append-only through the application context.");

        var now = clock.GetUtcNow();
        foreach (var entry in ChangeTracker.Entries<RefreshToken>().Where(e => e.State is EntityState.Added or EntityState.Modified))
            entry.Entity.TokenHash = entry.Entity.TokenHash.ToUpperInvariant();
        foreach (var entry in ChangeTracker.Entries<AuthChallenge>().Where(e => e.State is EntityState.Added or EntityState.Modified))
            entry.Entity.SecretDigest = entry.Entity.SecretDigest.ToUpperInvariant();
        foreach (var entry in ChangeTracker.Entries<RecoveryCode>().Where(e => e.State is EntityState.Added or EntityState.Modified))
            entry.Entity.CodeHash = entry.Entity.CodeHash.ToUpperInvariant();
        foreach (var entry in ChangeTracker.Entries<User>().Where(e => e.State is EntityState.Added or EntityState.Modified))
        {
            entry.Entity.Email = entry.Entity.Email.Trim();
            entry.Entity.NormalizedEmail = EmailNormalizer.Normalize(entry.Entity.Email);
            entry.Entity.DisplayName = entry.Entity.DisplayName.Trim();
            entry.Entity.UpdatedAt = now;
            if (entry.State == EntityState.Added) entry.Entity.CreatedAt = now;
            else entry.Property(x => x.CreatedAt).IsModified = false;
        }
    }
}
