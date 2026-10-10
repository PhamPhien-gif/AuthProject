using AuthProject.Data;
using AuthProject.Entities;
using AuthProject.Repositories;
using AuthProject.Security.Interfaces;
using Microsoft.EntityFrameworkCore;
using AuthProject.Configuration;
using AuthProject.Logging;
using AuthProject.Security;
using AuthProject.TestSupport;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using AuthProject.Exceptions;

namespace AuthProject.IntegrationTests.Database;

[Trait("Category", "Integration")]
[Trait("Dependency", "SqlServer")]
public sealed class LoginStoreTests(SqlServerFixture fixture) : IClassFixture<SqlServerFixture>
{
    private sealed class Factory(string connection) : IDbContextFactory<AuthDbContext>
    {
        public AuthDbContext CreateDbContext() => new(new DbContextOptionsBuilder<AuthDbContext>().UseSqlServer(connection).Options);
    }
    private sealed class TestIssuer : IAccessTokenIssuer
    {
        public string Issue(AuthSession session) => "test-signed-token";
    }
    private LoginStore Store(IAccessTokenIssuer? issuer = null) => new(new Factory(fixture.ConnectionString), Options.Create(new JwtOptions()),
        new PasswordHashService(Options.Create(new PasswordHasherOptions { IterationCount = 210000 })), issuer ?? new TestIssuer(),
        new AuditPayloadProtector(new EphemeralDataProtectionProvider()), TimeProvider.System);
    [Fact]
    public async Task Login_CreatesSessionDigestHistoryAndCompletedAudit()
    {
        await using var db = new AuthDbContext(new DbContextOptionsBuilder<AuthDbContext>().UseSqlServer(fixture.ConnectionString).Options);
        await db.Database.MigrateAsync();
        var user = new User { Email = Guid.NewGuid() + "@example.test", DisplayName = "User", Status = UserStatus.Active, EmailConfirmedAt = DateTimeOffset.UtcNow, PasswordHash = "test-hash" };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var result = await Store().CompleteAsync(new(user.Id, user.AuthVersion, false), "test-password", "trace_login", "192.0.2.1", "test-client", default);
        Assert.NotNull(result);
        var session = await db.Sessions.SingleAsync(x => x.UserId == user.Id);
        Assert.Single(await db.RefreshTokens.Where(x => x.SessionId == session.Id).ToArrayAsync());
        Assert.Single(await db.LoginHistories.Where(x => x.UserId == user.Id && x.Outcome == AuditOutcome.Success).ToArrayAsync());
        Assert.Single(await db.AuditEvents.Where(x => x.SubjectId == user.Id && x.EventType == AuditEventType.LoginCompleted).ToArrayAsync());
    }

    private sealed class BrokenIssuer : IAccessTokenIssuer
    {
        public string Issue(AuthSession session) => throw new InvalidOperationException("Simulated signing failure.");
    }
    [Fact]
    public async Task SigningFailure_RollsBackRehashSessionRefreshHistoryAndAudit()
    {
        await using var db = new Factory(fixture.ConnectionString).CreateDbContext();
        await db.Database.MigrateAsync();
        var user = new User { Email = Guid.NewGuid() + "@example.test", DisplayName = "User", Status = UserStatus.Active, EmailConfirmedAt = DateTimeOffset.UtcNow };
        var password = "a long secure password 123!";
        user.PasswordHash = new PasswordHasher<User>(Options.Create(new PasswordHasherOptions { IterationCount = 10000 })).HashPassword(user, password);
        var original = user.PasswordHash;
        db.Users.Add(user);
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store(new BrokenIssuer()).CompleteAsync(new(user.Id, user.AuthVersion, true), password, "trace_rollback", null, null, default));
        Assert.False(await db.Sessions.AnyAsync(x => x.UserId == user.Id));
        Assert.False(await db.LoginHistories.AnyAsync(x => x.UserId == user.Id));
        Assert.False(await db.AuditEvents.AnyAsync(x => x.SubjectId == user.Id));
        Assert.Equal(original, (await db.Users.AsNoTracking().SingleAsync(x => x.Id == user.Id)).PasswordHash);
    }

    [Fact]
    public async Task ChangedAuthVersion_BlocksSessionCompletionAfterPrimaryFactorAccepted()
    {
        await using var db = new Factory(fixture.ConnectionString).CreateDbContext();
        await db.Database.MigrateAsync();
        var user = new User { Email = Guid.NewGuid() + "@example.test", DisplayName = "User", Status = UserStatus.Active,
            EmailConfirmedAt = DateTimeOffset.UtcNow, PasswordHash = "test-hash", AuthVersion = 2 };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        Assert.Null(await Store().CompleteAsync(new(user.Id, 1, false), "test-password", "trace_stale", null, null, default));
        Assert.False(await db.Sessions.AnyAsync(x => x.UserId == user.Id));
    }
}
