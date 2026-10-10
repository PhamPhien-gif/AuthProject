using AuthProject.Configuration;
using AuthProject.Data;
using AuthProject.DTOs;
using AuthProject.Entities;
using AuthProject.Logging;
using AuthProject.Repositories;
using AuthProject.Security;
using AuthProject.Security.Interfaces;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace AuthProject.IntegrationTests.Database;

[Trait("Category", "Integration"), Trait("Dependency", "SqlServer")]
public sealed class RefreshSessionTests(SqlServerFixture fixture) : IClassFixture<SqlServerFixture>
{
    private sealed class Factory(string connection) : IDbContextFactory<AuthDbContext>
    {
        public AuthDbContext CreateDbContext() => new(new DbContextOptionsBuilder<AuthDbContext>().UseSqlServer(connection).Options);
    }
    private sealed class Issuer(bool fail = false) : IAccessTokenIssuer
    {
        public string Issue(AuthSession session) => fail ? throw new InvalidOperationException("signing failed") : "signed-access";
    }
    private readonly FakeTimeProvider clock = new(new DateTimeOffset(2026, 10, 10, 0, 0, 0, TimeSpan.Zero));
    private readonly IAuditPayloadProtector protector = new AuditPayloadProtector(new EphemeralDataProtectionProvider());
    private readonly IOptions<JwtOptions> jwt = Options.Create(new JwtOptions());
    private Factory DbFactory => new(fixture.ConnectionString);
    private LoginStore LoginStore => new(DbFactory, jwt, new PasswordHashService(Options.Create(new PasswordHasherOptions())), new Issuer(), protector, clock);
    private SessionStore Store(bool fail = false, BruteForceOptions? rate = null, IAuditPayloadProtector? audit = null) => new(DbFactory, jwt, new Issuer(fail), audit ?? protector, clock,
        Options.Create(rate ?? new BruteForceOptions { Enabled = false }), new BoundedRateLimitStore(clock, Options.Create(rate ?? new BruteForceOptions())), new RateLimitKeyFactory());
    private async Task<(User User, SessionGrant Grant, Guid Session)> Seed(User? existing = null)
    {
        await using var db = DbFactory.CreateDbContext();
        await db.Database.MigrateAsync();
        var user = existing ?? new User { Email = Guid.NewGuid() + "@example.test", DisplayName = "Owner", Status = UserStatus.Active,
            EmailConfirmedAt = clock.GetUtcNow(), PasswordHash = "not-used-for-direct-store-tests" };
        if (existing is null) { db.Users.Add(user); db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = AuthProject.Data.Configurations.SystemRoles.UserId }); await db.SaveChangesAsync(); }
        var grant = (await LoginStore.CompleteAsync(new(user.Id, user.AuthVersion, false), "unused", "seed", null, null, default))!;
        var session = await db.RefreshTokens.AsNoTracking().SingleAsync(x => x.TokenHash == RefreshTokenSecret.Digest(grant.RefreshToken));
        return (user, grant, session.SessionId);
    }
    [Fact]
    public async Task Rotation_ConsumesLinksAndExtendsIdleWithoutExtendingAbsoluteExpiry()
    {
        var (_, grant, id) = await Seed();
        clock.Advance(TimeSpan.FromDays(1));
        var result = await Store().RefreshAsync(RefreshTokenSecret.Digest(grant.RefreshToken)!, "rotation", default);
        Assert.NotNull(result.Grant);
        Assert.NotEqual(grant.RefreshToken, result.Grant.RefreshToken);
        await using var db = DbFactory.CreateDbContext();
        var old = await db.RefreshTokens.SingleAsync(x => x.TokenHash == RefreshTokenSecret.Digest(grant.RefreshToken));
        var next = await db.RefreshTokens.SingleAsync(x => x.Id == old.ReplacedById);
        Assert.NotNull(old.ConsumedAt);
        Assert.Null(next.ConsumedAt);
        var session = await db.Sessions.SingleAsync(x => x.Id == id);
        Assert.Equal(session.CreatedAt.AddDays(30), session.AbsoluteExpiresAt);
        Assert.Equal(clock.GetUtcNow().AddDays(7), session.IdleExpiresAt);
        Assert.Equal(clock.GetUtcNow(), session.LastSeenAt);
        Assert.Single(await db.RefreshTokens.Where(x => x.SessionId == id && x.ConsumedAt == null && x.RevokedAt == null).ToArrayAsync());
        Assert.Contains(await db.AuditEvents.Where(x => x.SubjectId == session.UserId).ToArrayAsync(), x => x.EventType == AuditEventType.RefreshRotated);
        Assert.DoesNotContain(grant.RefreshToken, string.Join("", await db.AuditEvents.Select(x => x.SafeMetadata).ToArrayAsync()));
    }
    [Fact]
    public async Task ConsumedTokenReplay_CommitsFamilyRevocationBeforeReturningFailure()
    {
        var (user, grant, id) = await Seed();
        var store = Store();
        var next = (await store.RefreshAsync(RefreshTokenSecret.Digest(grant.RefreshToken)!, "first", default)).Grant!;
        Assert.Null((await store.RefreshAsync(RefreshTokenSecret.Digest(grant.RefreshToken)!, "reuse", default)).Grant);
        Assert.Null((await store.RefreshAsync(RefreshTokenSecret.Digest(next.RefreshToken)!, "after_reuse", default)).Grant);
        Assert.Null(await LoginStore.ValidateSessionAsync(user.Id, id, user.AuthVersion, default));
        await using var db = DbFactory.CreateDbContext();
        Assert.Equal("RefreshReuse", (await db.Sessions.SingleAsync(x => x.Id == id)).RevokeReason);
        Assert.All(await db.RefreshTokens.Where(x => x.SessionId == id).ToArrayAsync(), x => Assert.NotNull(x.RevokedAt));
        Assert.Single(await db.AuditEvents.Where(x => x.SubjectId == user.Id && x.EventType == AuditEventType.RefreshReused).ToArrayAsync());
    }
    [Fact]
    public async Task ParallelRefresh_ExactlyOneWinsAndLoserRevokesItsSuccessor()
    {
        var (_, grant, id) = await Seed();
        var store = Store();
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => store.RefreshAsync(RefreshTokenSecret.Digest(grant.RefreshToken)!, "parallel", default)));
        Assert.Single(results, x => x.Grant is not null);
        await using var db = DbFactory.CreateDbContext();
        Assert.NotNull((await db.Sessions.SingleAsync(x => x.Id == id)).RevokedAt);
        Assert.All(await db.RefreshTokens.Where(x => x.SessionId == id).ToArrayAsync(), x => Assert.NotNull(x.RevokedAt));
    }
    [Fact]
    public async Task RefreshRacingLogout_CannotLeaveAnActiveFamily()
    {
        var (_, grant, id) = await Seed();
        var store = Store();
        var digest = RefreshTokenSecret.Digest(grant.RefreshToken)!;
        await Task.WhenAll(store.RefreshAsync(digest, "race_refresh", default), store.LogoutAsync(digest, "race_logout", default));
        await store.LogoutAsync(digest, "repeat_logout", default);
        await using var db = DbFactory.CreateDbContext();
        Assert.NotNull((await db.Sessions.SingleAsync(x => x.Id == id)).RevokedAt);
        Assert.False(await db.RefreshTokens.AnyAsync(x => x.SessionId == id && x.RevokedAt == null));
        Assert.Single(await db.AuditEvents.Where(x => x.TraceId == "race_logout" && x.SubjectId == (db.Sessions.Where(s => s.Id == id).Select(s => s.UserId).First())).ToArrayAsync());
    }
    [Theory]
    [InlineData("idle")]
    [InlineData("absolute")]
    [InlineData("token-expired")]
    [InlineData("disabled")]
    [InlineData("unverified")]
    [InlineData("version")]
    [InlineData("mfa")]
    public async Task InvalidAccountOrExpiry_CannotRotate(string state)
    {
        var (user, grant, id) = await Seed();
        await using var db = DbFactory.CreateDbContext();
        var account = await db.Users.SingleAsync(x => x.Id == user.Id);
        var session = await db.Sessions.SingleAsync(x => x.Id == id);
        if (state == "disabled") account.Status = UserStatus.Disabled;
        if (state == "unverified") account.EmailConfirmedAt = null;
        if (state == "version") account.AuthVersion++;
        if (state == "mfa") account.MfaEmailEnabled = true;
        if (state == "absolute") session.IdleExpiresAt = session.AbsoluteExpiresAt = session.CreatedAt.AddSeconds(1);
        if (state == "token-expired") (await db.RefreshTokens.SingleAsync(x => x.SessionId == id)).ExpiresAt = session.CreatedAt.AddSeconds(1);
        await db.SaveChangesAsync();
        if (state == "idle") clock.Advance(TimeSpan.FromDays(7));
        if (state is "absolute" or "token-expired") clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Null((await Store().RefreshAsync(RefreshTokenSecret.Digest(grant.RefreshToken)!, "invalid", default)).Grant);
        Assert.Single(await db.RefreshTokens.Where(x => x.SessionId == id).ToArrayAsync());
    }
    [Fact]
    public async Task SigningFailure_RollsBackConsumptionAndLeavesOriginalUsable()
    {
        var (_, grant, id) = await Seed();
        var digest = RefreshTokenSecret.Digest(grant.RefreshToken)!;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store(fail: true).RefreshAsync(digest, "broken_signer", default));
        await using var db = DbFactory.CreateDbContext();
        Assert.Null((await db.RefreshTokens.SingleAsync(x => x.SessionId == id)).ConsumedAt);
        Assert.NotNull((await Store().RefreshAsync(digest, "retry", default)).Grant);
    }
    [Fact]
    public async Task LogoutAll_RevokesOnlyOwnerAndInvalidatesStalePrimaryAcceptance()
    {
        var first = await Seed();
        var second = await Seed(first.User);
        var foreign = await Seed();
        await Store().LogoutAllAsync(first.User.Id, "all", default);
        Assert.Null(await LoginStore.ValidateSessionAsync(first.User.Id, first.Session, first.User.AuthVersion, default));
        Assert.Null(await LoginStore.ValidateSessionAsync(first.User.Id, second.Session, first.User.AuthVersion, default));
        Assert.NotNull(await LoginStore.ValidateSessionAsync(foreign.User.Id, foreign.Session, foreign.User.AuthVersion, default));
        Assert.Null(await LoginStore.CompleteAsync(new(first.User.Id, first.User.AuthVersion, false), "unused", "stale", null, null, default));
    }
    [Fact]
    public async Task RevokeAndList_AreOwnerScopedAndRevocationIsIdempotent()
    {
        var first = await Seed();
        var foreign = await Seed();
        var store = Store();
        var page = await store.ListAsync(first.User.Id, first.Session, new PagingRequest(), default);
        Assert.Equal(first.Session, Assert.Single(page.Items).Id);
        Assert.True(page.Items[0].IsCurrent);
        Assert.False(await store.RevokeOwnedAsync(first.User.Id, foreign.Session, "idor", default));
        Assert.True(await store.RevokeOwnedAsync(first.User.Id, first.Session, "owned", default));
        Assert.True(await store.RevokeOwnedAsync(first.User.Id, first.Session, "repeat", default));
        Assert.NotNull(await LoginStore.ValidateSessionAsync(foreign.User.Id, foreign.Session, foreign.User.AuthVersion, default));
    }
    [Fact]
    public async Task VerifiedCookieSessionQuota_ReturnsRetryWithoutConsumingToken()
    {
        var (_, grant, _) = await Seed();
        var rate = new BruteForceOptions();
        rate.Policies["Refresh"].SessionPermitLimit = 1;
        var store = Store(rate: rate);
        var next = (await store.RefreshAsync(RefreshTokenSecret.Digest(grant.RefreshToken)!, "quota_first", default)).Grant!;
        var limited = await store.RefreshAsync(RefreshTokenSecret.Digest(next.RefreshToken)!, "quota_limited", default);
        Assert.Null(limited.Grant);
        Assert.Equal(60, limited.RetryAfterSeconds);
        clock.Advance(TimeSpan.FromSeconds(60));
        Assert.NotNull((await store.RefreshAsync(RefreshTokenSecret.Digest(next.RefreshToken)!, "quota_retry", default)).Grant);
    }

    private sealed class BrokenProtector : IAuditPayloadProtector
    {
        public string Protect(AuditEnvelope envelope) => throw new InvalidOperationException("audit unavailable");
        public AuditEnvelope Unprotect(string value) => throw new NotSupportedException();
    }
    [Fact]
    public async Task AuditFailureAfterConsume_RollsBackOldTokenAndIdleUpdate()
    {
        var (_, grant, id) = await Seed();
        clock.Advance(TimeSpan.FromHours(1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store(audit: new BrokenProtector()).RefreshAsync(RefreshTokenSecret.Digest(grant.RefreshToken)!, "audit_failure", default));
        await using var db = DbFactory.CreateDbContext();
        var old = await db.RefreshTokens.SingleAsync(x => x.SessionId == id);
        Assert.Null(old.ConsumedAt); Assert.Null(old.ReplacedById);
        Assert.Equal(old.CreatedAt, (await db.Sessions.SingleAsync(x => x.Id == id)).LastSeenAt);
        Assert.False(await db.AuditEvents.AnyAsync(x => x.TraceId == "audit_failure"));
        Assert.NotNull((await Store().RefreshAsync(old.TokenHash, "after_audit_failure", default)).Grant);
    }
    [Fact]
    public async Task NearAbsoluteExpiry_ClampsCookieIdleAndAccessLifetime()
    {
        var (_, grant, id) = await Seed();
        var store = Store();
        // Keep the idle window alive until the final minute of the absolute lifetime.
        for (var day = 0; day < 5; day++)
        {
            clock.Advance(TimeSpan.FromDays(day == 4 ? 5 : 6));
            grant = (await store.RefreshAsync(RefreshTokenSecret.Digest(grant.RefreshToken)!, "keep_alive", default)).Grant!;
        }
        clock.Advance(TimeSpan.FromDays(1) - TimeSpan.FromMinutes(1));
        var last = (await store.RefreshAsync(RefreshTokenSecret.Digest(grant.RefreshToken)!, "absolute_cap", default)).Grant!;
        Assert.Equal(60, last.Response.ExpiresIn);
        await using var db = DbFactory.CreateDbContext();
        var session = await db.Sessions.SingleAsync(x => x.Id == id);
        Assert.Equal(session.AbsoluteExpiresAt, session.IdleExpiresAt);
        Assert.Equal(session.AbsoluteExpiresAt, last.ExpiresAt);
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Null((await store.RefreshAsync(RefreshTokenSecret.Digest(last.RefreshToken)!, "absolute_boundary", default)).Grant);
    }
    [Fact]
    public async Task ConsumedTombstoneDetectsReuseAfterOriginalExpiryAndWhenQuotaIsFull()
    {
        var (_, grant, id) = await Seed();
        var rate = new BruteForceOptions(); rate.Policies["Refresh"].SessionPermitLimit = 1;
        var store = Store(rate: rate);
        clock.Advance(TimeSpan.FromDays(6));
        var next = (await store.RefreshAsync(RefreshTokenSecret.Digest(grant.RefreshToken)!, "extend", default)).Grant!;
        clock.Advance(TimeSpan.FromDays(2));
        // Fill the current window, then replay the tombstone whose old expiry is already past.
        Assert.NotNull((await store.RefreshAsync(RefreshTokenSecret.Digest(next.RefreshToken)!, "fill_quota", default)).Grant);
        var replay = await store.RefreshAsync(RefreshTokenSecret.Digest(grant.RefreshToken)!, "expired_tombstone", default);
        Assert.Null(replay.Grant); Assert.Null(replay.RetryAfterSeconds);
        await using var db = DbFactory.CreateDbContext();
        Assert.NotNull((await db.Sessions.SingleAsync(x => x.Id == id)).RevokedAt);
    }
}
