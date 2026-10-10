using AuthProject.Configuration;
using AuthProject.Data;
using AuthProject.Data.Configurations;
using AuthProject.DTOs;
using AuthProject.Entities;
using AuthProject.Exceptions;
using AuthProject.Logging;
using AuthProject.Repositories;
using AuthProject.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace AuthProject.IntegrationTests.Database;

[Trait("Category", "Integration"), Trait("Dependency", "SqlServer")]
public sealed class AdminStoreTests(SqlServerFixture fixture) : IClassFixture<SqlServerFixture>
{
    private readonly FakeTimeProvider clock = new(new DateTimeOffset(2026, 10, 11, 0, 0, 0, TimeSpan.Zero));
    private sealed class Factory(string connection, TimeProvider clock) : IDbContextFactory<AuthDbContext>
    { public AuthDbContext CreateDbContext() => new(new DbContextOptionsBuilder<AuthDbContext>().UseSqlServer(connection).Options, clock); }
    private Factory DbFactory => new(fixture.ConnectionString, clock);
    private AdminStore Store() => new(DbFactory, new AuditPayloadProtector(new EphemeralDataProtectionProvider()), clock, Options.Create(new EmailOtpOptions()));
    private async Task<PasswordActor> Seed(bool admin = true)
    {
        await using var db = DbFactory.CreateDbContext(); await db.Database.MigrateAsync();
        var now = clock.GetUtcNow(); var user = new User { Email = Guid.NewGuid() + "@example.test", DisplayName = "Actor", Status = UserStatus.Active, EmailConfirmedAt = now, MfaEmailEnabled = true };
        var session = new AuthSession { UserId = user.Id, AuthVersion = user.AuthVersion, AuthenticatedAt = now, MfaVerifiedAt = now,
            CreatedAt = now, LastSeenAt = now, IdleExpiresAt = now.AddDays(1), AbsoluteExpiresAt = now.AddDays(2) };
        db.Users.Add(user); db.Sessions.Add(session); db.UserRoles.Add(new() { UserId = user.Id, RoleId = admin ? SystemRoles.AdminId : SystemRoles.UserId });
        db.RefreshTokens.Add(new() { SessionId = session.Id, TokenHash = RefreshTokenSecret.Digest(RefreshTokenSecret.Create())!, CreatedAt = now, ExpiresAt = session.IdleExpiresAt });
        db.Challenges.Add(new() { UserId = user.Id, AuthVersion = user.AuthVersion, Purpose = ChallengePurpose.GoogleCompletion, SecretDigest = new string('A', 64), CreatedAt = now, ExpiresAt = now.AddMinutes(1) });
        await db.SaveChangesAsync(); return new(user.Id, session.Id, user.AuthVersion);
    }
    [Fact]
    public async Task Roles_change_revokes_tokens_challenges_and_audits_atomically()
    {
        var actor = await Seed(); var target = await Seed(false);
        await Store().SetRolesAsync(actor, target.UserId, new([SystemRoles.User, SystemRoles.Admin]), "roles", default);
        await using var db = DbFactory.CreateDbContext(); Assert.Equal(2, (await db.Users.SingleAsync(u => u.Id == target.UserId)).AuthVersion);
        Assert.NotNull((await db.Sessions.SingleAsync(s => s.Id == target.SessionId)).RevokedAt);
        Assert.NotNull((await db.RefreshTokens.SingleAsync(t => t.SessionId == target.SessionId)).RevokedAt);
        Assert.All(await db.Challenges.Where(c => c.UserId == target.UserId).ToArrayAsync(), c => Assert.NotNull(c.ConsumedAt));
        Assert.Equal(2, await db.UserRoles.CountAsync(r => r.UserId == target.UserId));
        Assert.Single(await db.AuditEvents.Where(a => a.TraceId == "roles" && a.EventType == AuditEventType.RoleChanged).ToArrayAsync());
    }
    [Fact]
    public async Task Self_escalation_and_non_admin_store_call_are_forbidden()
    {
        var actor = await Seed(false);
        Assert.Equal("AUTH_FORBIDDEN", (await Assert.ThrowsAsync<AppException>(() => Store().SetRolesAsync(actor, actor.UserId, new([SystemRoles.Admin]), "self", default))).Error.Code);
    }
    [Fact]
    public async Task Last_active_admin_cannot_be_disabled_or_demoted()
    {
        var actor = await Seed();
        // This test fixture is shared across cases: isolate the invariant to this actor.
        await using (var setup = DbFactory.CreateDbContext())
            await setup.Users.Where(u => u.Id != actor.UserId).ExecuteUpdateAsync(s => s.SetProperty(u => u.Status, UserStatus.Disabled));
        Assert.Equal("AUTH_LAST_ADMIN", (await Assert.ThrowsAsync<AppException>(() => Store().SetStatusAsync(actor, actor.UserId, new("Disabled"), "last", default))).Error.Code);
        Assert.Equal("AUTH_LAST_ADMIN", (await Assert.ThrowsAsync<AppException>(() => Store().SetRolesAsync(actor, actor.UserId, new([SystemRoles.User]), "last", default))).Error.Code);
    }
    [Fact]
    public async Task Concurrent_admin_removals_leave_one_active_admin()
    {
        var a = await Seed(); var b = await Seed();
        await using (var setup = DbFactory.CreateDbContext())
            await setup.Users.Where(u => u.Id != a.UserId && u.Id != b.UserId).ExecuteUpdateAsync(s => s.SetProperty(u => u.Status, UserStatus.Disabled));
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<string?> Disable(PasswordActor actor, PasswordActor target)
        { await ready.Task; try { await Store().SetStatusAsync(actor, target.UserId, new("Disabled"), "parallel", default); return null; } catch (AppException ex) { return ex.Error.Code; } }
        var first = Disable(a, b); var second = Disable(b, a); ready.SetResult(); var results = await Task.WhenAll(first, second);
        Assert.Single(results, r => r is null); Assert.Single(results, r => r is not null);
        await using var db = DbFactory.CreateDbContext(); Assert.Equal(1, await db.Users.CountAsync(u => (u.Id == a.UserId || u.Id == b.UserId) && u.Status == UserStatus.Active));
    }
    private sealed class BrokenAudit : IAuditPayloadProtector
    {
        public string Protect(AuditEnvelope value) => throw new InvalidOperationException("audit unavailable");
        public AuditEnvelope Unprotect(string value) => throw new NotSupportedException();
    }
    [Fact]
    public async Task Audit_failure_rolls_back_roles_version_and_bulk_revocation()
    {
        var actor = await Seed(); var target = await Seed(false);
        var broken = new AdminStore(DbFactory, new BrokenAudit(), clock, Options.Create(new EmailOtpOptions()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => broken.SetRolesAsync(actor, target.UserId, new([SystemRoles.Admin]), "rollback", default));
        await using var db = DbFactory.CreateDbContext(); Assert.Equal(1, (await db.Users.SingleAsync(u => u.Id == target.UserId)).AuthVersion);
        Assert.Null((await db.Sessions.SingleAsync(s => s.Id == target.SessionId)).RevokedAt);
        Assert.Null((await db.RefreshTokens.SingleAsync(t => t.SessionId == target.SessionId)).RevokedAt);
        Assert.False(await db.UserRoles.AnyAsync(r => r.UserId == target.UserId && r.RoleId == SystemRoles.AdminId));
        Assert.False(await db.AuditEvents.AnyAsync(a => a.TraceId == "rollback"));
    }
    [Fact]
    public async Task Role_noop_preserves_session_but_admin_cannot_modify_own_roles()
    {
        var actor = await Seed(); var other = await Seed();
        await Store().SetRolesAsync(actor, other.UserId, new([SystemRoles.Admin]), "noop", default);
        Assert.Equal("AUTH_FORBIDDEN", (await Assert.ThrowsAsync<AppException>(() => Store().SetRolesAsync(actor, actor.UserId, new([SystemRoles.Admin, SystemRoles.User]), "self_admin", default))).Error.Code);
        await using var db = DbFactory.CreateDbContext(); Assert.Null((await db.Sessions.SingleAsync(s => s.Id == other.SessionId)).RevokedAt);
        Assert.False(await db.AuditEvents.AnyAsync(a => a.TraceId == "noop"));
    }
}
