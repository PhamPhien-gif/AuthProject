using AuthProject.Configuration;
using AuthProject.Data;
using AuthProject.Entities;
using AuthProject.Exceptions;
using AuthProject.Logging;
using AuthProject.Repositories;
using AuthProject.Security;
using AuthProject.Security.Interfaces;
using AuthProject.Services.Email;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace AuthProject.IntegrationTests.Database;

[Trait("Category", "Integration"), Trait("Dependency", "SqlServer")]
public sealed class GoogleStoreTests(SqlServerFixture fixture) : IClassFixture<SqlServerFixture>
{
    private readonly FakeTimeProvider clock = new(new DateTimeOffset(2026, 10, 10, 0, 0, 0, TimeSpan.Zero));
    private readonly AuditPayloadProtector audits = new(new EphemeralDataProtectionProvider());
    private readonly EmailOtpOptions otp = new() { Enabled = true, KeyId = "test", Pepper = Convert.ToBase64String(new byte[32]) };
    private sealed class Factory(string connection, TimeProvider clock) : IDbContextFactory<AuthDbContext>
    { public AuthDbContext CreateDbContext() => new(new DbContextOptionsBuilder<AuthDbContext>().UseSqlServer(connection).Options, clock); }
    private Factory DbFactory => new(fixture.ConnectionString, clock);
    private sealed class Issuer(bool fail = false) : IAccessTokenIssuer { public string Issue(AuthSession session) => fail ? throw new InvalidOperationException("signing unavailable") : "google-access"; }
    private GoogleStore Store(bool failSigning = false)
    {
        var issuer = new Issuer(failSigning);
        var mfa = new MfaStore(DbFactory, Options.Create(otp), Options.Create(new JwtOptions()), new(Options.Create(otp)),
            new(new EphemeralDataProtectionProvider()), audits, issuer, new PasswordHashService(Options.Create(new Microsoft.AspNetCore.Identity.PasswordHasherOptions())), clock);
        return new(DbFactory, Options.Create(new JwtOptions()), Options.Create(otp), mfa, issuer, audits, clock);
    }
    private async Task<GoogleIdentity> Identity()
    {
        await using var db = DbFactory.CreateDbContext(); await db.Database.MigrateAsync();
        return new(Guid.NewGuid().ToString("N"), Guid.NewGuid() + "@example.test", "Google User", clock.GetUtcNow().ToUnixTimeSeconds());
    }
    private GoogleBinding Binding(GoogleFlow flow = GoogleFlow.Login, PasswordActor? actor = null) => new(Guid.NewGuid(), flow, clock.GetUtcNow(), actor);
    [Fact]
    public async Task New_identity_creates_verified_User_role_but_no_session_until_completion()
    {
        var identity = await Identity(); var grant = await Store().StageAsync(identity, Binding(), "callback", default);
        await using var db = DbFactory.CreateDbContext(); var user = await db.Users.SingleAsync(u => u.Id == grant.Binding.UserId!.Value);
        Assert.Null(user.PasswordHash); Assert.NotNull(user.EmailConfirmedAt); Assert.Equal(UserStatus.Active, user.Status);
        Assert.Single(await db.UserRoles.Where(r => r.UserId == user.Id).ToArrayAsync()); Assert.False(await db.Sessions.AnyAsync(s => s.UserId == user.Id));
        var result = await Store().CompleteAsync(grant.Binding, grant.Ticket, null, "complete", default);
        Assert.NotNull(result.Session); Assert.Equal(AuthenticationMethod.Google, (await db.Sessions.SingleAsync(s => s.UserId == user.Id)).PrimaryMethod);
        Assert.Equal("AUTH_CHALLENGE_INVALID", (await Assert.ThrowsAsync<AppException>(() => Store().CompleteAsync(grant.Binding, grant.Ticket, null, "replay", default))).Error.Code);
    }
    [Fact]
    public async Task Email_collision_does_not_auto_link()
    {
        var identity = await Identity(); await using var db = DbFactory.CreateDbContext();
        var user = new User { Email = identity.Email, DisplayName = "Local", PasswordHash = "existing", Status = UserStatus.Active, EmailConfirmedAt = clock.GetUtcNow() };
        db.Users.Add(user); await db.SaveChangesAsync();
        Assert.Equal("AUTH_GOOGLE_LINK_REQUIRED", (await Assert.ThrowsAsync<AppException>(() => Store().StageAsync(identity, Binding(), "collision", default))).Error.Code);
        Assert.False(await db.ExternalLogins.AnyAsync(x => x.UserId == user.Id));
    }
    [Fact]
    public async Task Google_MFA_completion_returns_only_challenge_with_Google_primary()
    {
        var identity = await Identity(); var first = await Store().StageAsync(identity, Binding(), "register", default);
        await using var db = DbFactory.CreateDbContext(); var user = await db.Users.SingleAsync(u => u.Id == first.Binding.UserId!.Value);
        user.MfaEmailEnabled = true; user.AuthVersion++; await db.SaveChangesAsync();
        var grant = await Store().StageAsync(identity, Binding(), "mfa_callback", default);
        var result = await Store().CompleteAsync(grant.Binding, grant.Ticket, null, "mfa_complete", default);
        Assert.Null(result.Session); Assert.NotNull(result.Challenge); Assert.False(await db.Sessions.AnyAsync(s => s.UserId == user.Id));
        var challenge = await db.Challenges.SingleAsync(c => c.Id == result.Challenge.Response.ChallengeId);
        Assert.Equal(AuthenticationMethod.Google, challenge.PrimaryMethod);
    }
    [Fact]
    public async Task Completion_requires_browser_binding_and_fresh_version()
    {
        var identity = await Identity(); var grant = await Store().StageAsync(identity, Binding(), "callback", default);
        await Assert.ThrowsAsync<AppException>(() => Store().CompleteAsync(grant.Binding with { ContextId = Guid.NewGuid() }, grant.Ticket, null, "wrong_browser", default));
        clock.Advance(TimeSpan.FromSeconds(60));
        await Assert.ThrowsAsync<AppException>(() => Store().CompleteAsync(grant.Binding, grant.Ticket, null, "expired", default));
    }
    [Fact]
    public async Task Google_only_account_cannot_unlink_last_login_method()
    {
        var identity = await Identity(); var grant = await Store().StageAsync(identity, Binding(), "callback", default);
        await Store().CompleteAsync(grant.Binding, grant.Ticket, null, "complete", default);
        await using var db = DbFactory.CreateDbContext(); var session = await db.Sessions.SingleAsync(s => s.UserId == grant.Binding.UserId!.Value);
        Assert.Equal("AUTH_LAST_LOGIN_METHOD", (await Assert.ThrowsAsync<AppException>(() => Store().UnlinkAsync(new(session.UserId, session.Id, session.AuthVersion), "unlink", default))).Error.Code);
    }
    [Fact]
    public async Task Concurrent_completions_have_one_session_winner()
    {
        var identity = await Identity(); var grant = await Store().StageAsync(identity, Binding(), "callback", default);
        async Task<bool> Complete() { try { await Store().CompleteAsync(grant.Binding, grant.Ticket, null, "parallel", default); return true; }
            catch (AppException ex) when (ex.Error.Code == "AUTH_CHALLENGE_INVALID") { return false; } }
        Assert.Single(await Task.WhenAll(Complete(), Complete()), x => x);
        await using var db = DbFactory.CreateDbContext(); Assert.Single(await db.Sessions.Where(s => s.UserId == grant.Binding.UserId).ToArrayAsync());
    }
    [Fact]
    public async Task Signing_failure_rolls_back_completion_consume_and_session()
    {
        var identity = await Identity(); var grant = await Store().StageAsync(identity, Binding(), "callback", default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store(true).CompleteAsync(grant.Binding, grant.Ticket, null, "rollback", default));
        await using var db = DbFactory.CreateDbContext(); Assert.Null((await db.Challenges.SingleAsync(c => c.Id == grant.Binding.ChallengeId)).ConsumedAt);
        Assert.False(await db.Sessions.AnyAsync(s => s.UserId == grant.Binding.UserId)); Assert.False(await db.AuditEvents.AnyAsync(a => a.TraceId == "rollback"));
    }
    [Theory]
    [InlineData("version")] [InlineData("disabled")] [InlineData("binding")] [InlineData("ticket")]
    public async Task Changed_state_invalidates_completion(string state)
    {
        var identity = await Identity(); var grant = await Store().StageAsync(identity, Binding(), "callback", default);
        await using var db = DbFactory.CreateDbContext(); var user = await db.Users.SingleAsync(u => u.Id == grant.Binding.UserId);
        if (state == "version") user.AuthVersion++;
        if (state == "disabled") user.Status = UserStatus.Disabled;
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<AppException>(() => Store().CompleteAsync(state == "binding" ? grant.Binding with { Subject = "attacker" } : grant.Binding,
            state == "ticket" ? RefreshTokenSecret.Create() : grant.Ticket, null, "invalid", default));
        Assert.False(await db.Sessions.AnyAsync(s => s.UserId == user.Id));
    }
    [Fact]
    public async Task Subject_is_stable_when_provider_email_changes()
    {
        var identity = await Identity(); var first = await Store().StageAsync(identity, Binding(), "first", default);
        var second = await Store().StageAsync(identity with { Email = Guid.NewGuid() + "@example.test" }, Binding(), "changed_email", default);
        Assert.Equal(first.Binding.UserId, second.Binding.UserId);
        await using var db = DbFactory.CreateDbContext(); Assert.Equal(identity.Email, (await db.Users.SingleAsync(u => u.Id == first.Binding.UserId)).Email);
    }
    private async Task<PasswordActor> LocalActor(bool mfa = false)
    {
        var identity = await Identity(); await using var db = DbFactory.CreateDbContext(); var now = clock.GetUtcNow();
        var user = new User { Email = identity.Email, DisplayName = "Local", PasswordHash = "local-password-hash", Status = UserStatus.Active, EmailConfirmedAt = now, MfaEmailEnabled = mfa };
        db.Users.Add(user); var session = new AuthSession { UserId = user.Id, AuthVersion = user.AuthVersion, AuthenticatedAt = now,
            MfaVerifiedAt = mfa ? now.AddMinutes(-6) : null, CreatedAt = now, LastSeenAt = now, IdleExpiresAt = now.AddDays(7), AbsoluteExpiresAt = now.AddDays(30) };
        db.Sessions.Add(session); await db.SaveChangesAsync(); return new(user.Id, session.Id, user.AuthVersion);
    }
    [Fact]
    public async Task Existing_subject_cannot_be_taken_over_by_explicit_link()
    {
        var identity = await Identity(); var existing = await Store().StageAsync(identity, Binding(), "owner", default); var actor = await LocalActor();
        Assert.Equal("RESOURCE_CONFLICT", (await Assert.ThrowsAsync<AppException>(() => Store().StageAsync(identity, Binding(GoogleFlow.Link, actor), "takeover", default))).Error.Code);
        await using var db = DbFactory.CreateDbContext(); Assert.Equal(existing.Binding.UserId, (await db.ExternalLogins.SingleAsync(x => x.ProviderSubject == identity.Subject)).UserId);
    }
    [Fact]
    public async Task Link_rechecks_recent_primary_and_current_MFA_factor()
    {
        var actor = await LocalActor(true);
        Assert.Equal("AUTH_STEP_UP_REQUIRED", (await Assert.ThrowsAsync<AppException>(() => Store().AuthorizeActorAsync(actor, true, default))).Error.Code);
        var local = await LocalActor(); clock.Advance(TimeSpan.FromSeconds(300));
        Assert.Equal("AUTH_STEP_UP_REQUIRED", (await Assert.ThrowsAsync<AppException>(() => Store().AuthorizeActorAsync(local, true, default))).Error.Code);
    }
    [Fact]
    public async Task Slow_reauthentication_cannot_make_old_provider_auth_time_recent()
    {
        var identity = await Identity(); var registration = await Store().StageAsync(identity, Binding(), "register", default);
        await Store().CompleteAsync(registration.Binding, registration.Ticket, null, "login", default);
        await using var db = DbFactory.CreateDbContext(); var session = await db.Sessions.SingleAsync(s => s.UserId == registration.Binding.UserId);
        var binding = Binding(GoogleFlow.Reauthenticate, new(session.UserId, session.Id, session.AuthVersion));
        clock.Advance(TimeSpan.FromSeconds(301));
        await Assert.ThrowsAsync<AppException>(() => Store().StageAsync(identity, binding, "slow_provider", default));
    }
}

