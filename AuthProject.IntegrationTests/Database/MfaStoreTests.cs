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
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AuthProject.IntegrationTests.Database;

[Trait("Category", "Integration"), Trait("Dependency", "SqlServer")]
public sealed class MfaStoreTests(SqlServerFixture fixture) : IClassFixture<SqlServerFixture>
{
    private const string Password = "a long secure password 123!";
    private readonly FakeTimeProvider clock = new(new DateTimeOffset(2026, 10, 10, 0, 0, 0, TimeSpan.Zero));
    private readonly EmailOtpOptions options = new() { Enabled = true, Pepper = Convert.ToBase64String(new byte[32]), KeyId = "test" };
    private readonly MfaEmailProtector emails = new(new EphemeralDataProtectionProvider());
    private readonly AuditPayloadProtector audits = new(new EphemeralDataProtectionProvider());
    private readonly PasswordHashService hashes = new(Options.Create(new PasswordHasherOptions { IterationCount = 210000 }));
    private sealed class Factory(string connection, TimeProvider clock, IInterceptor? interceptor = null) : IDbContextFactory<AuthDbContext>
    {
        public AuthDbContext CreateDbContext()
        {
            var builder = new DbContextOptionsBuilder<AuthDbContext>().UseSqlServer(connection);
            if (interceptor is not null) builder.AddInterceptors(interceptor);
            return new(builder.Options, clock);
        }
    }
    private Factory DbFactory => new(fixture.ConnectionString, clock);
    private sealed class Issuer(bool fail = false) : IAccessTokenIssuer
    { public string Issue(AuthSession session) => fail ? throw new InvalidOperationException("sign failure") : "test-access"; }
    private MfaStore Store(bool failSigning = false, IAuditPayloadProtector? audit = null, IDbContextFactory<AuthDbContext>? factory = null) => new(factory ?? DbFactory, Options.Create(options), Options.Create(new JwtOptions()),
        new(Options.Create(options)), emails, audit ?? audits, new Issuer(failSigning), hashes, clock);
    private async Task<(User User, PasswordActor Actor)> Seed(bool mfa = true)
    {
        await using var db = DbFactory.CreateDbContext(); await db.Database.MigrateAsync();
        await db.OutboxMessages.Where(m => m.Kind == OutboxKind.MfaEmail && m.ProcessedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.ProcessedAt, clock.GetUtcNow()));
        var now = clock.GetUtcNow();
        var user = new User { Email = Guid.NewGuid() + "@example.test", DisplayName = "User", Status = UserStatus.Active,
            EmailConfirmedAt = now, MfaEmailEnabled = mfa };
        user.PasswordHash = hashes.Hash(user, Password); db.Users.Add(user);
        var session = new AuthSession { UserId = user.Id, AuthVersion = user.AuthVersion, AuthenticatedAt = now,
            MfaVerifiedAt = mfa ? now : null, CreatedAt = now, LastSeenAt = now, IdleExpiresAt = now.AddDays(7), AbsoluteExpiresAt = now.AddDays(30) };
        db.Sessions.Add(session);
        db.RefreshTokens.Add(new RefreshToken { SessionId = session.Id, TokenHash = RefreshTokenSecret.Digest(RefreshTokenSecret.Create())!,
            CreatedAt = now, ExpiresAt = session.IdleExpiresAt });
        await db.SaveChangesAsync(); return (user, new(user.Id, session.Id, user.AuthVersion));
    }
    private Task<MfaChallengeGrant?> Begin(User user) => Store().BeginAsync(user.Id, ChallengePurpose.LoginMfa,
        null, new(user.Id, user.AuthVersion, false), Password, "begin", default);
    private async Task<MfaEmail> Mail(Guid challengeId)
    {
        await using var db = DbFactory.CreateDbContext();
        var userId = await db.Challenges.Where(c => c.Id == challengeId).Select(c => c.UserId).SingleAsync();
        var rows = await (from row in db.OutboxMessages.AsNoTracking() join audit in db.AuditEvents on row.EventId equals audit.Id
            where row.Kind == OutboxKind.MfaEmail && audit.SubjectId == userId select row).ToArrayAsync();
        return rows.Select(m => emails.Unprotect(m.ProtectedPayload)).Where(m => m.ChallengeId == challengeId).MaxBy(m => m.Version)!;
    }
    private static async Task Error(string expected, Func<Task> action)
    { Assert.Equal(expected, (await Assert.ThrowsAsync<AppException>(action)).Error.Code); }
    [Fact]
    public async Task Primary_creates_only_bound_challenge_and_encrypted_email()
    {
        var (user, _) = await Seed(); var grant = (await Begin(user))!; var mail = await Mail(grant.Response.ChallengeId);
        await using var db = DbFactory.CreateDbContext();
        Assert.Equal(1, await db.Sessions.CountAsync(s => s.UserId == user.Id)); // Only the seeded session.
        var challenge = await db.Challenges.SingleAsync(c => c.Id == grant.Response.ChallengeId);
        Assert.NotEqual(mail.Code, challenge.SecretDigest); Assert.Equal("test", challenge.KeyId);
        var payload = await (from row in db.OutboxMessages join audit in db.AuditEvents on row.EventId equals audit.Id
            where row.Kind == OutboxKind.MfaEmail && audit.SubjectId == user.Id select row.ProtectedPayload).SingleAsync();
        Assert.DoesNotContain(mail.Code, payload);
        Assert.DoesNotContain(mail.Code, string.Join("", await db.AuditEvents.Where(a => a.SubjectId == user.Id).Select(a => a.SafeMetadata).ToArrayAsync()));
    }
    [Fact]
    public async Task Valid_OTP_issues_MFA_session_once_with_original_primary_time()
    {
        var (user, _) = await Seed(); var grant = (await Begin(user))!; var mail = await Mail(grant.Response.ChallengeId);
        clock.Advance(TimeSpan.FromSeconds(60));
        var result = await Store().VerifyAsync(grant.Ticket, null, mail.Code, false, "valid", default);
        Assert.NotNull(result.Session); Assert.NotNull(result.Session.RefreshToken);
        await Error("AUTH_CHALLENGE_INVALID", () => Store().VerifyAsync(grant.Ticket, null, mail.Code, false, "replay", default));
        await using var db = DbFactory.CreateDbContext();
        var session = await db.Sessions.SingleAsync(s => s.UserId == user.Id && s.CreatedAt == clock.GetUtcNow());
        Assert.Equal(mail.ExpiresAt.AddSeconds(-options.ExpirySeconds), session.AuthenticatedAt);
        Assert.Equal(clock.GetUtcNow(), session.MfaVerifiedAt);
    }
    [Theory]
    [InlineData("expiry")] [InlineData("deadline")] [InlineData("purpose")] [InlineData("context")]
    [InlineData("version")] [InlineData("disabled")] [InlineData("key")] [InlineData("actor")]
    public async Task Invalid_bindings_and_state_do_not_create_session(string state)
    {
        var (user, actor) = await Seed(); var grant = (await Begin(user))!; var mail = await Mail(grant.Response.ChallengeId);
        var ticket = grant.Ticket;
        await using var db = DbFactory.CreateDbContext();
        if (state == "expiry") clock.Advance(TimeSpan.FromSeconds(options.ExpirySeconds));
        if (state == "deadline") clock.Advance(TimeSpan.FromSeconds(options.ChallengeLifetimeSeconds));
        if (state == "purpose") ticket = ticket with { Purpose = ChallengePurpose.EnrollMfa };
        if (state == "context") ticket = ticket with { ContextId = Guid.NewGuid() };
        if (state == "version") (await db.Users.SingleAsync(u => u.Id == user.Id)).AuthVersion++;
        if (state == "disabled") (await db.Users.SingleAsync(u => u.Id == user.Id)).Status = UserStatus.Disabled;
        if (state == "key") (await db.Challenges.SingleAsync(c => c.Id == mail.ChallengeId)).KeyId = "retired";
        await db.SaveChangesAsync();
        await Error("AUTH_CHALLENGE_INVALID", () => Store().VerifyAsync(ticket, state == "actor" ? actor : null, mail.Code, false, "invalid", default));
        Assert.Equal(1, await db.Sessions.CountAsync(s => s.UserId == user.Id));
    }
    [Fact]
    public async Task Concurrent_verify_has_one_winner()
    {
        var (user, _) = await Seed(); var grant = (await Begin(user))!; var mail = await Mail(grant.Response.ChallengeId);
        async Task<bool> Verify() { try { await Store().VerifyAsync(grant.Ticket, null, mail.Code, false, "parallel", default); return true; }
            catch (AppException ex) when (ex.Error.Code == "AUTH_CHALLENGE_INVALID") { return false; } }
        var results = await Task.WhenAll(Verify(), Verify()); Assert.Single(results, x => x);
        await using var db = DbFactory.CreateDbContext(); Assert.Equal(2, await db.Sessions.CountAsync(s => s.UserId == user.Id));
    }
    [Fact]
    public async Task Resend_invalidates_old_OTP_preserves_attempts_and_caps_sends_deadline()
    {
        var (user, _) = await Seed(); var grant = (await Begin(user))!; var first = await Mail(grant.Response.ChallengeId);
        var wrong = first.Code == "000000" ? "000001" : "000000";
        await Error("AUTH_CHALLENGE_INVALID", () => Store().VerifyAsync(grant.Ticket, null, wrong, false, "wrong", default));
        await Error("RATE_LIMITED", () => Store().ResendAsync(grant.Ticket, null, "early", default));
        clock.Advance(TimeSpan.FromSeconds(60)); await Store().ResendAsync(grant.Ticket, null, "resend", default);
        await Error("AUTH_CHALLENGE_INVALID", () => Store().VerifyAsync(grant.Ticket, null, first.Code, false, "old", default));
        await using var db = DbFactory.CreateDbContext(); var challenge = await db.Challenges.AsNoTracking().SingleAsync(c => c.Id == first.ChallengeId);
        Assert.Equal(2, challenge.Attempts); Assert.Equal(2, challenge.Version); Assert.Equal(2, challenge.SendCount);
        clock.Advance(TimeSpan.FromSeconds(300)); var last = await Store().ResendAsync(grant.Ticket, null, "last", default);
        Assert.Equal(challenge.CreatedAt.AddSeconds(options.ChallengeLifetimeSeconds), last.Response.ExpiresAt);
        clock.Advance(TimeSpan.FromSeconds(60)); await Error("RATE_LIMITED", () => Store().ResendAsync(grant.Ticket, null, "budget", default));
        await Error("RATE_LIMITED", () => Begin(user));
    }
    [Fact]
    public async Task Attempt_cap_blocks_correct_code_and_resend()
    {
        var (user, _) = await Seed(); var grant = (await Begin(user))!; var mail = await Mail(grant.Response.ChallengeId);
        for (var i = 0; i < options.MaxAttempts; i++) await Error("AUTH_CHALLENGE_INVALID", () => Store().VerifyAsync(grant.Ticket,
            null, mail.Code == "000000" ? "000001" : "000000", false, "failure", default));
        await Error("AUTH_CHALLENGE_INVALID", () => Store().VerifyAsync(grant.Ticket, null, mail.Code, false, "capped", default));
        clock.Advance(TimeSpan.FromSeconds(60)); await Error("AUTH_CHALLENGE_INVALID", () => Store().ResendAsync(grant.Ticket, null, "capped_resend", default));
    }
    [Fact]
    public async Task Enroll_requires_recent_primary_and_returns_hash_only_recovery_codes_revoking_sessions()
    {
        var (user, actor) = await Seed(false);
        var grant = (await Store().BeginAsync(user.Id, ChallengePurpose.EnrollMfa, actor, null, null, "enroll", default))!;
        var mail = await Mail(grant.Response.ChallengeId);
        await Error("AUTH_CHALLENGE_INVALID", () => Store().VerifyAsync(grant.Ticket, null, mail.Code, false, "anonymous", default));
        await Error("AUTH_CHALLENGE_INVALID", () => Store().VerifyAsync(grant.Ticket, actor, RefreshTokenSecret.Create(), true, "recovery_enroll", default));
        var result = await Store().VerifyAsync(grant.Ticket, actor, mail.Code, false, "enabled", default);
        Assert.Equal(options.RecoveryCodeCount, result.Recovery!.RecoveryCodes.Length); Assert.Null(result.Session);
        await using var db = DbFactory.CreateDbContext(); var current = await db.Users.SingleAsync(u => u.Id == user.Id);
        Assert.True(current.MfaEmailEnabled); Assert.Equal(user.AuthVersion + 1, current.AuthVersion);
        Assert.NotNull((await db.Sessions.SingleAsync(s => s.Id == actor.SessionId)).RevokedAt);
        Assert.All(await db.RefreshTokens.Where(t => t.SessionId == actor.SessionId).ToArrayAsync(), t => Assert.NotNull(t.RevokedAt));
        var hashes = await db.RecoveryCodes.Where(c => c.UserId == user.Id).Select(c => c.CodeHash).ToArrayAsync();
        Assert.All(result.Recovery.RecoveryCodes, code => { Assert.DoesNotContain(code, hashes); Assert.Contains(RefreshTokenSecret.Digest(code), hashes); });
        await Error("AUTH_CHALLENGE_INVALID", () => Store().VerifyAsync(grant.Ticket, actor, mail.Code, false, "again", default));
    }
    [Fact]
    public async Task Enrollment_rejects_stale_primary_proof()
    {
        var (user, actor) = await Seed(false); clock.Advance(TimeSpan.FromSeconds(300));
        await Error("AUTH_STEP_UP_REQUIRED", () => Store().BeginAsync(user.Id, ChallengePurpose.EnrollMfa, actor, null, null, "stale", default));
    }
    [Fact]
    public async Task Recovery_code_is_one_use_and_cannot_cross_user()
    {
        var (user, _) = await Seed(); var (other, _) = await Seed(); var code = RefreshTokenSecret.Create();
        await using (var db = DbFactory.CreateDbContext())
        { db.RecoveryCodes.Add(new RecoveryCode { UserId = user.Id, CodeHash = RefreshTokenSecret.Digest(code)!, CreatedAt = clock.GetUtcNow() }); await db.SaveChangesAsync(); }
        var foreign = (await Begin(other))!;
        await Error("AUTH_CHALLENGE_INVALID", () => Store().VerifyAsync(foreign.Ticket, null, code, true, "foreign", default));
        var first = (await Begin(user))!; await Store().VerifyAsync(first.Ticket, null, code, true, "recovery", default);
        clock.Advance(TimeSpan.FromSeconds(60)); var second = (await Begin(user))!;
        await Error("AUTH_CHALLENGE_INVALID", () => Store().VerifyAsync(second.Ticket, null, code, true, "reused", default));
        await using var check = DbFactory.CreateDbContext(); Assert.NotNull((await check.RecoveryCodes.SingleAsync(c => c.UserId == user.Id)).UsedAt);
    }
    [Fact]
    public async Task Signing_failure_rolls_back_factor_and_recovery_consumption()
    {
        var (user, _) = await Seed(); var code = RefreshTokenSecret.Create();
        await using (var db = DbFactory.CreateDbContext())
        { db.RecoveryCodes.Add(new RecoveryCode { UserId = user.Id, CodeHash = RefreshTokenSecret.Digest(code)!, CreatedAt = clock.GetUtcNow() }); await db.SaveChangesAsync(); }
        var grant = (await Begin(user))!;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store(true).VerifyAsync(grant.Ticket, null, code, true, "rollback", default));
        await using var check = DbFactory.CreateDbContext();
        Assert.Null((await check.Challenges.SingleAsync(c => c.Id == grant.Ticket.ChallengeId)).ConsumedAt);
        Assert.Null((await check.RecoveryCodes.SingleAsync(c => c.UserId == user.Id)).UsedAt);
        Assert.Equal(1, await check.Sessions.CountAsync(s => s.UserId == user.Id)); Assert.False(await check.AuditEvents.AnyAsync(a => a.TraceId == "rollback"));
    }
    [Fact]
    public async Task Step_up_renews_both_proofs_then_disable_revokes_all()
    {
        var (user, actor) = await Seed(); clock.Advance(TimeSpan.FromSeconds(301));
        await Error("AUTH_STEP_UP_REQUIRED", () => Store().ChangeAsync(actor, true, "no_factor", default));
        var grant = (await Store().BeginAsync(user.Id, ChallengePurpose.StepUp, actor, new(user.Id, user.AuthVersion, false), Password, "stepup", default))!;
        var mail = await Mail(grant.Response.ChallengeId);
        await Error("AUTH_STEP_UP_REQUIRED", () => Store().ChangeAsync(actor, true, "still_pending", default));
        await Store().VerifyAsync(grant.Ticket, actor, mail.Code, false, "stepup_verified", default);
        await Store().ChangeAsync(actor, true, "disable", default);
        await using var db = DbFactory.CreateDbContext(); Assert.False((await db.Users.SingleAsync(u => u.Id == user.Id)).MfaEmailEnabled);
        Assert.NotNull((await db.Sessions.SingleAsync(s => s.Id == actor.SessionId)).RevokedAt);
    }
    [Fact]
    public async Task Regeneration_invalidates_all_previous_codes_and_session()
    {
        var (user, actor) = await Seed(); var old = RefreshTokenSecret.Create();
        await using (var db = DbFactory.CreateDbContext())
        { db.RecoveryCodes.Add(new RecoveryCode { UserId = user.Id, CodeHash = RefreshTokenSecret.Digest(old)!, CreatedAt = clock.GetUtcNow() }); await db.SaveChangesAsync(); }
        var result = await Store().ChangeAsync(actor, false, "regenerate", default);
        Assert.Equal(options.RecoveryCodeCount, result!.RecoveryCodes.Length);
        await using var check = DbFactory.CreateDbContext(); Assert.NotNull((await check.RecoveryCodes.SingleAsync(c => c.CodeHash == RefreshTokenSecret.Digest(old))).UsedAt);
        Assert.NotNull((await check.Sessions.SingleAsync(s => s.Id == actor.SessionId)).RevokedAt);
    }
    private sealed class Transport : IEmailTransport
    {
        public List<EmailContent> Sent { get; } = [];
        public Task SendAsync(Guid id, string recipient, EmailContent content, CancellationToken ct) { Sent.Add(content); return Task.CompletedTask; }
    }
    [Fact]
    public async Task Dispatcher_discards_replaced_and_consumed_OTPs()
    {
        var (user, _) = await Seed(); var first = (await Begin(user))!; clock.Advance(TimeSpan.FromSeconds(60));
        await Store().ResendAsync(first.Ticket, null, "resend_mail", default);
        var transport = new Transport(); var dispatcher = new MfaEmailDispatcher(DbFactory, emails, transport, new(Options.Create(options)),
            Options.Create(new RegistrationOptions()), clock, NullLogger<MfaEmailDispatcher>.Instance);
        Assert.True(await dispatcher.DispatchOneAsync()); Assert.Empty(transport.Sent);
        Assert.True(await dispatcher.DispatchOneAsync()); Assert.Single(transport.Sent);
        clock.Advance(TimeSpan.FromSeconds(60)); await Store().ResendAsync(first.Ticket, null, "third_mail", default);
        var mail = await Mail(first.Response.ChallengeId); await Store().VerifyAsync(first.Ticket, null, mail.Code, false, "consumed", default);
        Assert.True(await dispatcher.DispatchOneAsync()); Assert.Single(transport.Sent);
    }
    [Fact]
    public async Task Concurrent_resend_stages_one_replacement_and_preserves_budget()
    {
        var (user, _) = await Seed(); var grant = (await Begin(user))!; clock.Advance(TimeSpan.FromSeconds(60));
        async Task<bool> Resend() { try { await Store().ResendAsync(grant.Ticket, null, "parallel_resend", default); return true; }
            catch (AppException ex) when (ex.Error.Code == "RATE_LIMITED") { return false; } }
        Assert.Single(await Task.WhenAll(Resend(), Resend()), x => x);
        await using var db = DbFactory.CreateDbContext(); var challenge = await db.Challenges.SingleAsync(c => c.Id == grant.Ticket.ChallengeId);
        Assert.Equal(2, challenge.SendCount); Assert.Equal(2, challenge.Version);
    }
    [Fact]
    public async Task New_challenge_cannot_hide_recent_resends_of_an_older_challenge()
    {
        var (user, _) = await Seed(); var grant = (await Begin(user))!;
        clock.Advance(TimeSpan.FromSeconds(60)); await Store().ResendAsync(grant.Ticket, null, "send_at_60", default);
        clock.Advance(TimeSpan.FromSeconds(530)); await Store().ResendAsync(grant.Ticket, null, "send_at_590", default);
        clock.Advance(TimeSpan.FromSeconds(11)); await Begin(user); // First send expired from rolling window, two resends remain.
        await Error("RATE_LIMITED", () => Begin(user));
    }
    private sealed class UserReadBarrier : DbCommandInterceptor
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        {
            if (command.CommandText.Contains("UPDLOCK", StringComparison.Ordinal))
            { Reached.TrySetResult(); await Release.Task.WaitAsync(ct); }
            return result;
        }
    }
    [Fact]
    public async Task OTP_expiring_while_waiting_for_user_lock_is_rejected()
    {
        var (user, _) = await Seed(); var grant = (await Begin(user))!; var mail = await Mail(grant.Ticket.ChallengeId);
        var barrier = new UserReadBarrier();
        var operation = Store(factory: new Factory(fixture.ConnectionString, clock, barrier))
            .VerifyAsync(grant.Ticket, null, mail.Code, false, "delayed_lock", default);
        await barrier.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        clock.Advance(TimeSpan.FromSeconds(options.ExpirySeconds)); barrier.Release.TrySetResult();
        await Error("AUTH_CHALLENGE_INVALID", async () => await operation);
    }
    [Fact]
    public async Task Concurrent_recovery_across_login_and_step_up_has_one_winner()
    {
        var (user, actor) = await Seed(); var code = RefreshTokenSecret.Create();
        await using (var db = DbFactory.CreateDbContext())
        { db.RecoveryCodes.Add(new RecoveryCode { UserId = user.Id, CodeHash = RefreshTokenSecret.Digest(code)!, CreatedAt = clock.GetUtcNow() }); await db.SaveChangesAsync(); }
        var login = (await Begin(user))!;
        var step = (await Store().BeginAsync(user.Id, ChallengePurpose.StepUp, actor, new(user.Id, user.AuthVersion, false), Password, "step", default))!;
        async Task<bool> Verify(MfaTicket ticket, PasswordActor? boundActor)
        { try { await Store().VerifyAsync(ticket, boundActor, code, true, "parallel_recovery", default); return true; }
            catch (AppException ex) when (ex.Error.Code == "AUTH_CHALLENGE_INVALID") { return false; } }
        Assert.Single(await Task.WhenAll(Verify(login.Ticket, null), Verify(step.Ticket, actor)), x => x);
    }
    private sealed class BrokenAudit : IAuditPayloadProtector
    {
        public string Protect(AuditEnvelope envelope) => throw new InvalidOperationException("audit unavailable");
        public AuditEnvelope Unprotect(string payload) => throw new NotSupportedException();
    }
    [Fact]
    public async Task Audit_failure_rolls_back_enrollment_flag_codes_and_consumption()
    {
        var (user, actor) = await Seed(false);
        var grant = (await Store().BeginAsync(user.Id, ChallengePurpose.EnrollMfa, actor, null, null, "enroll", default))!;
        var mail = await Mail(grant.Ticket.ChallengeId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store(audit: new BrokenAudit()).VerifyAsync(grant.Ticket, actor, mail.Code, false, "audit_rollback", default));
        await using var check = DbFactory.CreateDbContext(); Assert.False((await check.Users.SingleAsync(u => u.Id == user.Id)).MfaEmailEnabled);
        Assert.False(await check.RecoveryCodes.AnyAsync(c => c.UserId == user.Id));
        Assert.Null((await check.Challenges.SingleAsync(c => c.Id == grant.Ticket.ChallengeId)).ConsumedAt);
        Assert.Null((await check.Sessions.SingleAsync(s => s.Id == actor.SessionId)).RevokedAt);
    }
}
