using AuthProject.Configuration;
using AuthProject.Data;
using AuthProject.Entities;
using AuthProject.Logging;
using AuthProject.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace AuthProject.IntegrationTests.Database;

[Trait("Category", "Integration"), Trait("Dependency", "SqlServer")]
public sealed class AuditRetentionTests(SqlServerFixture fixture) : IClassFixture<SqlServerFixture>
{
    private readonly FakeTimeProvider clock = new(new DateTimeOffset(2026, 10, 10, 0, 0, 0, TimeSpan.Zero));
    private sealed class Factory(string connection, TimeProvider clock) : IDbContextFactory<AuthDbContext>
    { public AuthDbContext CreateDbContext() => new(new DbContextOptionsBuilder<AuthDbContext>().UseSqlServer(connection).Options, clock); }
    private sealed class Env : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing"; public string ApplicationName { get; set; } = "AuthProject";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory; public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
    private Factory DbFactory => new(fixture.ConnectionString, clock);
    private AuditRetentionService Service(AuditLogOptions? file = null) => new(DbFactory, Options.Create(new AuditRetentionOptions()), Options.Create(file ?? new()), new Env(), clock);
    private async Task<(Guid Session, Guid OldToken)> Family(bool live)
    {
        await using var db = DbFactory.CreateDbContext(); await db.Database.MigrateAsync(); var now = clock.GetUtcNow();
        var user = new User { Email = Guid.NewGuid() + "@example.test", DisplayName = "User", Status = UserStatus.Active, EmailConfirmedAt = now };
        var session = new AuthSession { UserId = user.Id, AuthVersion = 1, CreatedAt = now.AddDays(-20), LastSeenAt = now.AddDays(-19), AuthenticatedAt = now.AddDays(-20),
            IdleExpiresAt = live ? now.AddDays(1) : now.AddDays(-10), AbsoluteExpiresAt = live ? now.AddDays(10) : now.AddDays(-10) };
        var next = new RefreshToken { SessionId = session.Id, TokenHash = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"), CreatedAt = now.AddDays(-19), ExpiresAt = session.IdleExpiresAt };
        var old = new RefreshToken { SessionId = session.Id, TokenHash = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"), CreatedAt = now.AddDays(-20), ExpiresAt = now.AddDays(-19), ConsumedAt = now.AddDays(-19), ReplacedById = next.Id };
        db.Users.Add(user); db.Sessions.Add(session); db.RefreshTokens.AddRange(old, next);
        db.Challenges.Add(new() { UserId = user.Id, AuthVersion = 1, Purpose = ChallengePurpose.ResetPassword, SecretDigest = new string('A', 64), CreatedAt = now.AddDays(-20), ExpiresAt = now.AddDays(-19) });
        await db.SaveChangesAsync(); return (session.Id, old.Id);
    }
    [Fact]
    public async Task Live_family_keeps_consumed_refresh_tombstone_even_when_token_expired_long_ago()
    {
        var family = await Family(true); await Service().SweepAsync(); await using var db = DbFactory.CreateDbContext();
        Assert.True(await db.RefreshTokens.AnyAsync(t => t.Id == family.OldToken)); Assert.True(await db.Sessions.AnyAsync(s => s.Id == family.Session));
    }
    [Fact]
    public async Task Terminal_family_is_deleted_together_after_grace_without_self_FK_violation()
    {
        var family = await Family(false); await Service().SweepAsync(); await using var db = DbFactory.CreateDbContext();
        Assert.False(await db.RefreshTokens.AnyAsync(t => t.SessionId == family.Session)); Assert.False(await db.Sessions.AnyAsync(s => s.Id == family.Session));
    }
    [Fact]
    public async Task Retention_preserves_pending_or_dead_letter_audit_and_scrubs_legacy_PII()
    {
        await using var db = DbFactory.CreateDbContext(); await db.Database.MigrateAsync(); var now = clock.GetUtcNow();
        var writer = new AuditWriter(db, new AuditPayloadProtector(new EphemeralDataProtectionProvider()), new FakeTimeProvider(now.AddDays(-400)));
        var pending = writer.Stage(new(AuditEventType.LoginFailed, AuditOutcome.Failure, AuditReason.None, "pending"));
        var delivered = writer.Stage(new(AuditEventType.LoginFailed, AuditOutcome.Failure, AuditReason.None, "delivered"));
        await db.SaveChangesAsync();
        await db.OutboxMessages.Where(m => m.EventId == pending).ExecuteUpdateAsync(s => s.SetProperty(m => m.DeadLetteredAt, now));
        await db.OutboxMessages.Where(m => m.EventId == delivered).ExecuteUpdateAsync(s => s.SetProperty(m => m.ProcessedAt, now.AddDays(-100)));
        var oldHistory = new LoginHistory { OccurredAt = now.AddDays(-91), TraceId = "old" };
        var recent = new LoginHistory { OccurredAt = now, TraceId = "recent", IpAddress = "192.0.2.123", UserAgent = "SECRET_IN_AGENT" };
        db.LoginHistories.AddRange(oldHistory, recent); await db.SaveChangesAsync(); await Service().SweepAsync();
        Assert.True(await db.AuditEvents.AnyAsync(a => a.Id == pending)); Assert.False(await db.AuditEvents.AnyAsync(a => a.Id == delivered));
        Assert.False(await db.LoginHistories.AnyAsync(h => h.Id == oldHistory.Id));
        var scrubbed = await db.LoginHistories.AsNoTracking().SingleAsync(h => h.Id == recent.Id); Assert.Null(scrubbed.UserAgent); Assert.Equal("192.0.2.0/24", scrubbed.IpAddress);
    }
    [Fact]
    public async Task File_cleanup_only_removes_owned_old_audit_files()
    {
        await using var db = DbFactory.CreateDbContext(); await db.Database.MigrateAsync();
        var dir = Path.Combine(Path.GetTempPath(), "AuthProject_Test_Audit_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        var owned = Path.Combine(dir, "audit-test.jsonl.1"); var other = Path.Combine(dir, "other.txt"); var current = Path.Combine(dir, "audit-active.jsonl");
        await File.WriteAllTextAsync(owned, "old"); await File.WriteAllTextAsync(other, "keep"); await File.WriteAllTextAsync(current, "keep");
        File.SetLastWriteTimeUtc(owned, clock.GetUtcNow().AddDays(-31).UtcDateTime); File.SetLastWriteTimeUtc(other, clock.GetUtcNow().AddDays(-31).UtcDateTime);
        File.SetLastWriteTimeUtc(current, clock.GetUtcNow().UtcDateTime);
        await Service(new() { Enabled = true, Directory = dir }).SweepAsync(); Assert.False(File.Exists(owned)); Assert.True(File.Exists(other)); Assert.True(File.Exists(current));
        File.Delete(other); File.Delete(current); Directory.Delete(dir);
    }
}
