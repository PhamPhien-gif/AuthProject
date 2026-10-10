using AuthProject.Configuration;
using AuthProject.Data;
using AuthProject.Entities;
using AuthProject.Logging;
using AuthProject.Repositories;
using AuthProject.Repositories.Interfaces;
using AuthProject.Security;
using AuthProject.Services.Email;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace AuthProject.IntegrationTests.Database;

[Trait("Category", "Integration"), Trait("Dependency", "SqlServer")]
public sealed class PasswordRecoveryTests(SqlServerFixture fixture) : IClassFixture<SqlServerFixture>
{
    private const string OldPassword = "a long secure password 123!", NewPassword = "a different secure password 456!";
    private readonly FakeTimeProvider clock = new(new DateTimeOffset(2026, 10, 10, 0, 0, 0, TimeSpan.Zero));
    private readonly PasswordRecoveryOptions options = new() { Enabled = true, ResetUrl = "https://client.example.test/reset" };
    private readonly PasswordEmailProtector emails = new(new EphemeralDataProtectionProvider());
    private readonly AuditPayloadProtector audits = new(new EphemeralDataProtectionProvider());
    private readonly PasswordHashService hashes = new(Options.Create(new PasswordHasherOptions { IterationCount = 210000 }));
    private sealed class Factory(string connection, TimeProvider clock) : IDbContextFactory<AuthDbContext>
    {
        public AuthDbContext CreateDbContext() => new(new DbContextOptionsBuilder<AuthDbContext>().UseSqlServer(connection).Options, clock);
    }
    private Factory DbFactory => new(fixture.ConnectionString, clock);
    private PasswordRecoveryStore Store(IAuditPayloadProtector? audit = null, IPasswordEmailProtector? mail = null) => new(DbFactory, Options.Create(options), hashes, mail ?? emails, audit ?? audits, clock);
    private async Task<(User User, AuthSession Session)> Seed(string state = "active")
    {
        await using var db = DbFactory.CreateDbContext(); await db.Database.MigrateAsync();
        await db.OutboxMessages.Where(m => m.Kind == OutboxKind.PasswordEmail && m.ProcessedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.ProcessedAt, clock.GetUtcNow()));
        var user = new User { Email = Guid.NewGuid() + "@example.test", DisplayName = "User", Status = UserStatus.Active,
            EmailConfirmedAt = clock.GetUtcNow(), MfaEmailEnabled = state == "mfa" };
        user.PasswordHash = state == "google" ? null : hashes.Hash(user, OldPassword);
        if (state == "disabled") user.Status = UserStatus.Disabled;
        if (state == "pending") { user.Status = UserStatus.PendingVerification; user.EmailConfirmedAt = null; }
        db.Users.Add(user);
        var now = clock.GetUtcNow();
        var session = new AuthSession { UserId = user.Id, AuthVersion = user.AuthVersion, AuthenticatedAt = now, CreatedAt = now,
            LastSeenAt = now, IdleExpiresAt = now.AddDays(7), AbsoluteExpiresAt = now.AddDays(30) };
        db.Sessions.Add(session);
        db.RefreshTokens.Add(new RefreshToken { SessionId = session.Id, TokenHash = EmailVerificationToken.Digest(EmailVerificationToken.Create())!, CreatedAt = now, ExpiresAt = now.AddDays(7) });
        await db.SaveChangesAsync(); return (user, session);
    }
    private async Task<PasswordEmail> Mail(Guid user)
    {
        await using var db = DbFactory.CreateDbContext();
        var challenge = await db.Challenges.AsNoTracking().Where(c => c.UserId == user && c.Purpose == ChallengePurpose.ResetPassword)
            .OrderByDescending(c => c.CreatedAt).ThenBy(c => c.Id).FirstAsync();
        return emails.Unprotect((await db.OutboxMessages.AsNoTracking().SingleAsync(m => m.EventId == challenge.Id && m.Kind == OutboxKind.PasswordEmail)).ProtectedPayload);
    }
    [Fact]
    public async Task Forgot_ProtectedOutboxCooldownBudgetAndReplacementAreAtomic()
    {
        var (user, _) = await Seed(); var store = Store();
        await store.ForgotAsync(user.Email.ToUpperInvariant(), "forgot", default); var first = await Mail(user.Id);
        await store.ForgotAsync(user.Email, "cooldown", default);
        await using var db = DbFactory.CreateDbContext(); Assert.Single(await db.Challenges.Where(c => c.UserId == user.Id).ToArrayAsync());
        clock.Advance(TimeSpan.FromSeconds(60)); await store.ForgotAsync(user.Email, "resend", default); var second = await Mail(user.Id);
        Assert.NotEqual(first.Token, second.Token);
        Assert.False(await store.ResetAsync(EmailVerificationToken.Digest(first.Token)!, NewPassword, "old_link", default));
        clock.Advance(TimeSpan.FromSeconds(60)); await store.ForgotAsync(user.Email, "third", default);
        clock.Advance(TimeSpan.FromSeconds(60)); await store.ForgotAsync(user.Email, "budget", default);
        Assert.Equal(3, await db.Challenges.CountAsync(c => c.UserId == user.Id));
        Assert.DoesNotContain(first.Token!, string.Join("", await db.AuditEvents.Where(a => a.SubjectId == user.Id).Select(a => a.SafeMetadata).ToArrayAsync()));
    }
    [Theory]
    [InlineData("unknown")]
    [InlineData("google")]
    [InlineData("pending")]
    [InlineData("disabled")]
    public async Task Forgot_IneligibleAccountsNeverGetPasswordChallenge(string state)
    {
        var (user, _) = await Seed(state); await Store().ForgotAsync(state == "unknown" ? Guid.NewGuid() + "@example.test" : user.Email, "ineligible", default);
        await using var db = DbFactory.CreateDbContext(); Assert.False(await db.Challenges.AnyAsync(c => c.UserId == user.Id));
        Assert.Equal(user.PasswordHash, (await db.Users.SingleAsync(u => u.Id == user.Id)).PasswordHash);
    }
    [Fact]
    public async Task Reset_ChangesHashRevokesFamilyConsumesChallengesPreservesMfaAndNotifies()
    {
        var (user, session) = await Seed("mfa"); var store = Store(); await store.ForgotAsync(user.Email, "forgot_reset", default); var mail = await Mail(user.Id);
        await using (var db = DbFactory.CreateDbContext())
        {
            db.RecoveryCodes.Add(new RecoveryCode { UserId = user.Id, CodeHash = new string('A', 64), CreatedAt = clock.GetUtcNow() });
            var locked = await db.Users.SingleAsync(u => u.Id == user.Id);
            locked.FailedLoginCount = 5; locked.FailedLoginWindowStart = clock.GetUtcNow(); locked.LockoutEnd = clock.GetUtcNow().AddMinutes(15);
            await db.SaveChangesAsync();
        }
        Assert.True(await store.ResetAsync(EmailVerificationToken.Digest(mail.Token)!, NewPassword, "reset", default));
        Assert.False(await store.ResetAsync(EmailVerificationToken.Digest(mail.Token)!, OldPassword, "replay", default));
        await using var check = DbFactory.CreateDbContext(); var current = await check.Users.SingleAsync(u => u.Id == user.Id);
        Assert.Equal(user.AuthVersion + 1, current.AuthVersion); Assert.True(current.MfaEmailEnabled);
        Assert.Equal(0, current.FailedLoginCount); Assert.Null(current.FailedLoginWindowStart); Assert.Null(current.LockoutEnd);
        Assert.Equal(AuthProject.Security.Interfaces.PasswordCheck.Success, hashes.Verify(current, current.PasswordHash!, NewPassword));
        Assert.Equal(AuthProject.Security.Interfaces.PasswordCheck.Failed, hashes.Verify(current, current.PasswordHash!, OldPassword));
        Assert.NotNull((await check.Sessions.SingleAsync(s => s.Id == session.Id)).RevokedAt);
        Assert.All(await check.RefreshTokens.Where(t => t.SessionId == session.Id).ToArrayAsync(), t => Assert.NotNull(t.RevokedAt));
        Assert.All(await check.Challenges.Where(c => c.UserId == user.Id).ToArrayAsync(), c => Assert.NotNull(c.ConsumedAt));
        Assert.Null((await check.RecoveryCodes.SingleAsync(c => c.UserId == user.Id)).UsedAt);
        var notification = await check.OutboxMessages.SingleAsync(m => m.Kind == OutboxKind.PasswordEmail && m.EventId == check.AuditEvents.Where(a => a.SubjectId == user.Id && a.EventType == AuditEventType.PasswordReset).Select(a => a.Id).First());
        Assert.Equal(PasswordEmailPurpose.PasswordChanged, emails.Unprotect(notification.ProtectedPayload).Purpose);
        Assert.Single(await check.Sessions.Where(s => s.UserId == user.Id).ToArrayAsync()); // No auto-login.
    }
    [Theory]
    [InlineData("expired")]
    [InlineData("version")]
    [InlineData("purpose")]
    [InlineData("disabled")]
    public async Task Reset_InvalidTokenStateCannotChangePassword(string state)
    {
        var (user, _) = await Seed(); await Store().ForgotAsync(user.Email, "forgot", default); var mail = await Mail(user.Id);
        await using var db = DbFactory.CreateDbContext();
        var challenge = await db.Challenges.SingleAsync(c => c.Id == mail.ChallengeId);
        if (state == "expired") clock.Advance(TimeSpan.FromSeconds(options.TokenLifetimeSeconds));
        if (state == "version") (await db.Users.SingleAsync(u => u.Id == user.Id)).AuthVersion++;
        if (state == "purpose") challenge.Purpose = ChallengePurpose.VerifyEmail;
        if (state == "disabled") (await db.Users.SingleAsync(u => u.Id == user.Id)).Status = UserStatus.Disabled;
        await db.SaveChangesAsync(); Assert.False(await Store().ResetAsync(EmailVerificationToken.Digest(mail.Token)!, NewPassword, "invalid_reset", default));
        Assert.Equal(user.PasswordHash, (await db.Users.AsNoTracking().SingleAsync(u => u.Id == user.Id)).PasswordHash);
    }
    [Fact]
    public async Task ConcurrentReset_HasExactlyOneWinner()
    {
        var (user, _) = await Seed(); await Store().ForgotAsync(user.Email, "forgot", default); var mail = await Mail(user.Id);
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Store().ResetAsync(EmailVerificationToken.Digest(mail.Token)!, NewPassword, "parallel_reset", default)));
        Assert.Single(results, r => r);
        await using var db = DbFactory.CreateDbContext(); Assert.Single(await db.AuditEvents.Where(a => a.SubjectId == user.Id && a.EventType == AuditEventType.PasswordReset).ToArrayAsync());
    }
    [Fact]
    public async Task ConcurrentForgot_OnlyStagesOneChallengeDuringCooldown()
    {
        var (user, _) = await Seed();
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Store().ForgotAsync(user.Email, "parallel_forgot", default)));
        await using var db = DbFactory.CreateDbContext(); Assert.Single(await db.Challenges.Where(c => c.UserId == user.Id).ToArrayAsync());
    }
    private sealed class Issuer : AuthProject.Security.Interfaces.IAccessTokenIssuer
    {
        public string Issue(AuthSession session) => "test-access";
    }
    [Fact]
    public async Task ResetRacingRefresh_CannotLeaveAnyActiveSuccessor()
    {
        var (user, session) = await Seed(); await Store().ForgotAsync(user.Email, "forgot", default); var mail = await Mail(user.Id);
        await using var db = DbFactory.CreateDbContext(); var digest = (await db.RefreshTokens.SingleAsync(t => t.SessionId == session.Id)).TokenHash;
        var limiterOptions = Options.Create(new BruteForceOptions { Enabled = false });
        var refresh = new SessionStore(DbFactory, Options.Create(new JwtOptions()), new Issuer(), audits, clock, limiterOptions,
            new BoundedRateLimitStore(clock, limiterOptions), new RateLimitKeyFactory());
        await Task.WhenAll(Store().ResetAsync(EmailVerificationToken.Digest(mail.Token)!, NewPassword, "reset_race", default), refresh.RefreshAsync(digest, "refresh_race", default));
        Assert.NotNull((await db.Sessions.AsNoTracking().SingleAsync(s => s.Id == session.Id)).RevokedAt);
        Assert.False(await db.RefreshTokens.AnyAsync(t => t.SessionId == session.Id && t.RevokedAt == null));
    }
    private sealed class BrokenEmail : IPasswordEmailProtector
    {
        public string Protect(PasswordEmail email) => throw new InvalidOperationException("notification failure");
        public PasswordEmail Unprotect(string value) => throw new NotSupportedException();
    }
    [Fact]
    public async Task NotificationFailure_RollsBackResetStateAndCompletionAudit()
    {
        var (user, session) = await Seed(); await Store().ForgotAsync(user.Email, "forgot", default); var mail = await Mail(user.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store(mail: new BrokenEmail()).ResetAsync(EmailVerificationToken.Digest(mail.Token)!, NewPassword, "mail_rollback", default));
        await using var db = DbFactory.CreateDbContext(); Assert.Equal(user.PasswordHash, (await db.Users.SingleAsync(u => u.Id == user.Id)).PasswordHash);
        Assert.Null((await db.Challenges.SingleAsync(c => c.Id == mail.ChallengeId)).ConsumedAt);
        Assert.Null((await db.Sessions.SingleAsync(s => s.Id == session.Id)).RevokedAt);
        Assert.False(await db.AuditEvents.AnyAsync(a => a.TraceId == "mail_rollback"));
    }
    private sealed class BrokenAudit : IAuditPayloadProtector
    {
        public string Protect(AuditEnvelope envelope) => throw new InvalidOperationException("audit failure");
        public AuditEnvelope Unprotect(string value) => throw new NotSupportedException();
    }
    [Fact]
    public async Task AuditFailure_RollsBackHashConsumeVersionRevocationAndNotification()
    {
        var (user, session) = await Seed(); await Store().ForgotAsync(user.Email, "forgot", default); var mail = await Mail(user.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store(new BrokenAudit()).ResetAsync(EmailVerificationToken.Digest(mail.Token)!, NewPassword, "rollback", default));
        await using var db = DbFactory.CreateDbContext(); Assert.Equal(user.PasswordHash, (await db.Users.SingleAsync(u => u.Id == user.Id)).PasswordHash);
        Assert.Equal(user.AuthVersion, (await db.Users.SingleAsync(u => u.Id == user.Id)).AuthVersion);
        Assert.Null((await db.Challenges.SingleAsync(c => c.Id == mail.ChallengeId)).ConsumedAt); Assert.Null((await db.Sessions.SingleAsync(s => s.Id == session.Id)).RevokedAt);
        Assert.False(await db.AuditEvents.AnyAsync(a => a.TraceId == "rollback"));
        Assert.True(await Store().ResetAsync(EmailVerificationToken.Digest(mail.Token)!, NewPassword, "retry", default));
    }
    [Fact]
    public async Task RecentAuthExpired_ReauthenticateRenewsPrimaryOnlyThenChangeRevokes()
    {
        var (user, session) = await Seed(); var actor = new PasswordActor(user.Id, session.Id, user.AuthVersion); var accepted = new AuthProject.Security.Interfaces.PasswordAcceptance(user.Id, user.AuthVersion, false);
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(PasswordMutationResult.StepUpRequired, await Store().ChangeAsync(actor, accepted, NewPassword, "too_old", default));
        Assert.Equal(PasswordMutationResult.Success, await Store().ReauthenticateAsync(actor, accepted, "reauth", default));
        await using var db = DbFactory.CreateDbContext(); var fresh = await db.Sessions.SingleAsync(s => s.Id == session.Id);
        Assert.Equal(clock.GetUtcNow(), fresh.AuthenticatedAt); Assert.Null(fresh.MfaVerifiedAt); Assert.Equal(session.AbsoluteExpiresAt, fresh.AbsoluteExpiresAt);
        Assert.Equal(PasswordMutationResult.Success, await Store().ChangeAsync(actor, accepted, NewPassword, "change", default));
        Assert.NotNull((await db.Sessions.AsNoTracking().SingleAsync(s => s.Id == session.Id)).RevokedAt);
    }
    [Fact]
    public async Task PasswordReauthCannotRenewExpiredMfaProof()
    {
        var (user, session) = await Seed("mfa"); var actor = new PasswordActor(user.Id, session.Id, user.AuthVersion); var accepted = new AuthProject.Security.Interfaces.PasswordAcceptance(user.Id, user.AuthVersion, false);
        Assert.Equal(PasswordMutationResult.StepUpRequired, await Store().ReauthenticateAsync(actor, accepted, "no_factor", default));
        await using var db = DbFactory.CreateDbContext(); var tracked = await db.Sessions.SingleAsync(s => s.Id == session.Id);
        tracked.MfaVerifiedAt = clock.GetUtcNow(); await db.SaveChangesAsync(); clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(PasswordMutationResult.StepUpRequired, await Store().ReauthenticateAsync(actor, accepted, "old_factor", default));
        Assert.Equal(PasswordMutationResult.StepUpRequired, await Store().ChangeAsync(actor, accepted, NewPassword, "old_factor_change", default));
    }
    private sealed class Transport : IEmailTransport
    {
        public bool Fail { get; set; }
        public List<EmailContent> Sent { get; } = [];
        public Task SendAsync(Guid id, string recipient, EmailContent content, CancellationToken ct) { if (Fail) throw new IOException("SMTP_DOWN_SECRET"); Sent.Add(content); return Task.CompletedTask; }
    }
    private PasswordEmailDispatcher Dispatcher(Transport transport) => new(DbFactory, emails, transport, Options.Create(options), Options.Create(new RegistrationOptions()), clock, NullLogger<PasswordEmailDispatcher>.Instance);
    [Fact]
    public async Task EmailOutbox_RetryUsesSameMessageAndSuppressesReplacedResetLinks()
    {
        var (user, _) = await Seed(); await Store().ForgotAsync(user.Email, "forgot", default);
        var transport = new Transport { Fail = true }; var dispatcher = Dispatcher(transport);
        Assert.True(await dispatcher.DispatchOneAsync()); Assert.Empty(transport.Sent);
        clock.Advance(TimeSpan.FromSeconds(30)); transport.Fail = false; Assert.True(await dispatcher.DispatchOneAsync()); Assert.Single(transport.Sent);
        clock.Advance(TimeSpan.FromSeconds(30)); await Store().ForgotAsync(user.Email, "second_link", default);
        clock.Advance(TimeSpan.FromSeconds(60)); await Store().ForgotAsync(user.Email, "third_link", default);
        await dispatcher.DispatchOneAsync(); Assert.Single(transport.Sent); // Replaced second link discarded.
        await dispatcher.DispatchOneAsync(); Assert.Equal(2, transport.Sent.Count);
        var mail = await Mail(user.Id); await Store().ResetAsync(EmailVerificationToken.Digest(mail.Token)!, NewPassword, "reset", default);
        await dispatcher.DispatchOneAsync(); Assert.Equal(3, transport.Sent.Count);
        Assert.DoesNotContain("#token", transport.Sent.Last().TextBody);
    }
}
