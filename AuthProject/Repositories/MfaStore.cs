using AuthProject.Configuration;
using AuthProject.Data;
using AuthProject.DTOs;
using AuthProject.Entities;
using AuthProject.Exceptions;
using AuthProject.Logging;
using AuthProject.Security;
using AuthProject.Security.Interfaces;
using AuthProject.Services.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AuthProject.Repositories;

public sealed record MfaVerification(SessionGrant? Session = null, RecoveryCodesResponse? Recovery = null);

// Every mutation takes the user lock first, matching password/session operations.
public sealed class MfaStore(IDbContextFactory<AuthDbContext> factory, IOptions<EmailOtpOptions> options,
    IOptions<JwtOptions> jwt, EmailOtpSecret secrets, MfaEmailProtector emails, IAuditPayloadProtector audits,
    IAccessTokenIssuer tokens, IPasswordHashService passwords, TimeProvider clock)
{
    private EmailOtpOptions Limits => options.Value;
    internal async Task<MfaChallengeGrant> StageGoogleAsync(AuthDbContext db, User user, DateTimeOffset primaryAt,
        PasswordActor? actor, string trace, CancellationToken ct)
    {
        if (!Limits.Enabled) throw new AppException("AUTH_STEP_UP_REQUIRED");
        var now = clock.GetUtcNow();
        await CheckSendBudget(db, user.Id, now, ct);
        var purpose = actor is null ? ChallengePurpose.LoginMfa : ChallengePurpose.StepUp;
        await db.Challenges.Where(c => c.UserId == user.Id && c.Purpose == purpose && c.ConsumedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.ConsumedAt, now), ct);
        var challenge = new AuthChallenge { UserId = user.Id, AuthVersion = user.AuthVersion, Purpose = purpose,
            PrimaryMethod = AuthenticationMethod.Google, ContextId = Guid.NewGuid(), KeyId = Limits.KeyId,
            CreatedAt = primaryAt, ExpiresAt = new[] { now.AddSeconds(Limits.ExpirySeconds), primaryAt.AddSeconds(Limits.ChallengeLifetimeSeconds) }.Min(),
            SendCount = 1, NextSendAt = now.AddSeconds(Limits.ResendCooldownSeconds) };
        db.Challenges.Add(challenge); StageEmail(db, user, challenge, trace, now);
        return Grant(challenge, actor?.SessionId);
    }
    public async Task<MfaChallengeGrant?> BeginAsync(Guid userId, ChallengePurpose purpose, PasswordActor? actor,
        PasswordAcceptance? accepted, string? password, string trace, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var user = await LockUser(db, userId, ct);
        var now = clock.GetUtcNow();
        if (!Active(user, now)) throw new AppException("AUTH_INVALID_CREDENTIALS");
        AuthSession? session = null;
        if (actor is not null) session = await ActorSession(db, user!, actor, now, ct);
        if (accepted is not null && (accepted.UserId != user!.Id || accepted.AuthVersion != user.AuthVersion
            || user.PasswordHash is null)) throw new AppException("AUTH_INVALID_CREDENTIALS");
        if (purpose == ChallengePurpose.LoginMfa)
        {
            if (accepted is null || actor is not null) throw new AppException("AUTH_INVALID_CREDENTIALS");
            if (!user!.MfaEmailEnabled) return null;
        }
        else if (purpose == ChallengePurpose.EnrollMfa)
        {
            if (session is null || user!.MfaEmailEnabled) throw new AppException("RESOURCE_CONFLICT");
            if (!RecentAuthenticationRules.Primary(session, now, Limits.RecentAuthenticationSeconds)) throw new AppException("AUTH_STEP_UP_REQUIRED");
        }
        else if (purpose != ChallengePurpose.StepUp || session is null || accepted is null || !user!.MfaEmailEnabled)
            throw new AppException("AUTH_CHALLENGE_INVALID");
        if (accepted?.RequiresRehash == true)
        {
            if (password is null || passwords.Verify(user!, user!.PasswordHash!, password) == PasswordCheck.Failed)
                throw new AppException("AUTH_INVALID_CREDENTIALS");
            user!.PasswordHash = passwords.Hash(user, password);
        }
        await CheckSendBudget(db, user!.Id, now, ct);
        // A new primary attempt invalidates older challenges of the same operation.
        await db.Challenges.Where(c => c.UserId == user.Id && c.Purpose == purpose && c.ConsumedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.ConsumedAt, now), ct);
        var challenge = new AuthChallenge { UserId = user.Id, Purpose = purpose, ContextId = Guid.NewGuid(),
            AuthVersion = user.AuthVersion, KeyId = Limits.KeyId, CreatedAt = now,
            ExpiresAt = now.AddSeconds(Limits.ExpirySeconds), SendCount = 1, NextSendAt = now.AddSeconds(Limits.ResendCooldownSeconds) };
        db.Challenges.Add(challenge);
        StageEmail(db, user, challenge, trace, now);
        if (purpose == ChallengePurpose.LoginMfa) db.LoginHistories.Add(new LoginHistory { UserId = user.Id,
            Method = AuthenticationMethod.Password, Outcome = AuditOutcome.Pending, OccurredAt = now, TraceId = trace });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Grant(challenge, actor?.SessionId);
    }

    public async Task<MfaChallengeGrant> ResendAsync(MfaTicket ticket, PasswordActor? actor, string trace, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var user = await LockUser(db, ticket.UserId, ct);
        var now = clock.GetUtcNow();
        var challenge = await BoundChallenge(db, user, ticket, actor, now, ct, allowExpiredOtp: true);
        if (challenge.NextSendAt > now || challenge.SendCount >= Limits.MaxSends) throw new AppException("RATE_LIMITED");
        await CheckSendBudget(db, user!.Id, now, ct);
        string nextCode;
        do { nextCode = EmailOtpSecret.Create(); } while (secrets.Matches(challenge, nextCode, challenge.SecretDigest));
        challenge.Version++;
        challenge.SendCount++;
        challenge.NextSendAt = now.AddSeconds(Limits.ResendCooldownSeconds);
        challenge.ExpiresAt = new[] { now.AddSeconds(Limits.ExpirySeconds), challenge.CreatedAt.AddSeconds(Limits.ChallengeLifetimeSeconds) }.Min();
        StageEmail(db, user, challenge, trace, now, nextCode);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Grant(challenge, ticket.SessionId);
    }

    public async Task<MfaVerification> VerifyAsync(MfaTicket ticket, PasswordActor? actor, string code, bool recovery,
        string trace, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var user = await LockUser(db, ticket.UserId, ct);
        var now = clock.GetUtcNow();
        var challenge = await BoundChallenge(db, user, ticket, actor, now, ct, allowExpiredOtp: recovery);
        if (recovery && challenge.Purpose == ChallengePurpose.EnrollMfa) throw new AppException("AUTH_CHALLENGE_INVALID");
        RecoveryCode? recovered = null;
        if (recovery)
        {
            var digest = RefreshTokenSecret.Digest(code);
            if (digest is not null) recovered = await db.RecoveryCodes.SingleOrDefaultAsync(c => c.UserId == user!.Id
                && c.CodeHash == digest && c.UsedAt == null, ct);
        }
        var valid = recovery ? recovered is not null : secrets.Matches(challenge, code, challenge.SecretDigest);
        if (!valid)
        {
            challenge.Attempts++;
            if (challenge.Attempts >= Limits.MaxAttempts) challenge.ConsumedAt = now;
            Audit(db, AuditEventType.MfaFailed, user!.Id, trace, AuditOutcome.Failure, AuditReason.InvalidCredentials,
                actor?.SessionId, recovery ? AuthenticationMethod.RecoveryCode : AuthenticationMethod.EmailOtp);
            if (challenge.Purpose == ChallengePurpose.LoginMfa) db.LoginHistories.Add(new LoginHistory { UserId = user.Id,
                Method = recovery ? AuthenticationMethod.RecoveryCode : AuthenticationMethod.EmailOtp, Outcome = AuditOutcome.Failure,
                ReasonCode = AuditReason.InvalidCredentials, OccurredAt = now, TraceId = trace });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct); // Failure budget must survive the error response.
            throw new AppException("AUTH_CHALLENGE_INVALID");
        }
        if (recovered is not null) recovered.UsedAt = now;
        challenge.ConsumedAt = now;
        Audit(db, AuditEventType.MfaVerified, user!.Id, trace, method: recovery ? AuthenticationMethod.RecoveryCode : AuthenticationMethod.EmailOtp);
        MfaVerification result;
        if (challenge.Purpose == ChallengePurpose.LoginMfa)
            result = new(Session: await CompleteLogin(db, user, challenge, trace, now, ct));
        else if (challenge.Purpose == ChallengePurpose.EnrollMfa)
        {
            var session = await ActorSession(db, user, actor!, now, ct);
            if (!RecentAuthenticationRules.Primary(session, now, Limits.RecentAuthenticationSeconds)) throw new AppException("AUTH_STEP_UP_REQUIRED");
            user.MfaEmailEnabled = true;
            result = new(Recovery: await ReplaceRecoveryCodes(db, user.Id, now, ct));
            await RevokeForChange(db, user, "MfaEnabled", trace, now, ct);
            Audit(db, AuditEventType.MfaEnabled, user.Id, trace);
        }
        else
        {
            var session = await ActorSession(db, user, actor!, now, ct);
            session.AuthenticatedAt = challenge.CreatedAt;
            session.PrimaryMethod = challenge.PrimaryMethod;
            session.MfaVerifiedAt = now;
            Audit(db, AuditEventType.Reauthenticated, user.Id, trace, sessionId: session.Id);
            result = new();
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return result;
    }

    public async Task<RecoveryCodesResponse?> ChangeAsync(PasswordActor actor, bool disable, string trace, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var user = await LockUser(db, actor.UserId, ct);
        var now = clock.GetUtcNow();
        if (!Active(user, now)) throw new AppException("AUTH_SESSION_INVALID");
        var session = await ActorSession(db, user!, actor, now, ct);
        if (!user!.MfaEmailEnabled || !RecentAuthenticationRules.Primary(session, now, Limits.RecentAuthenticationSeconds)
            || !RecentAuthenticationRules.Mfa(session, now, Limits.RecentAuthenticationSeconds)) throw new AppException("AUTH_STEP_UP_REQUIRED");
        RecoveryCodesResponse? result = null;
        if (disable)
        {
            user.MfaEmailEnabled = false;
            await db.RecoveryCodes.Where(c => c.UserId == user.Id && c.UsedAt == null).ExecuteUpdateAsync(s => s.SetProperty(c => c.UsedAt, now), ct);
        }
        else result = await ReplaceRecoveryCodes(db, user.Id, now, ct);
        var reason = disable ? "MfaDisabled" : "RecoveryRegenerated";
        // Avoid tracked rowversion conflicts after bulk session revocation.
        db.Entry(session).State = EntityState.Detached;
        await RevokeForChange(db, user, reason, trace, now, ct);
        Audit(db, disable ? AuditEventType.MfaDisabled : AuditEventType.RecoveryCodesRegenerated, user.Id, trace);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return result;
    }

    private async Task<AuthChallenge> BoundChallenge(AuthDbContext db, User? user, MfaTicket ticket, PasswordActor? actor,
        DateTimeOffset now, CancellationToken ct, bool allowExpiredOtp)
    {
        if (!Active(user, now) || ticket.AuthVersion != user!.AuthVersion) throw new AppException("AUTH_CHALLENGE_INVALID");
        var challenge = await db.Challenges.SingleOrDefaultAsync(c => c.Id == ticket.ChallengeId, ct);
        if (challenge is null || challenge.UserId != user.Id || challenge.ContextId != ticket.ContextId || challenge.Purpose != ticket.Purpose
            || challenge.AuthVersion != user.AuthVersion || challenge.KeyId != Limits.KeyId || challenge.ConsumedAt != null
            || challenge.CreatedAt > now || challenge.CreatedAt.AddSeconds(Limits.ChallengeLifetimeSeconds) <= now
            || (!allowExpiredOtp && challenge.ExpiresAt <= now) || challenge.Attempts >= Limits.MaxAttempts
            || challenge.Purpose is not (ChallengePurpose.LoginMfa or ChallengePurpose.EnrollMfa or ChallengePurpose.StepUp)
            || (challenge.Purpose == ChallengePurpose.EnrollMfa ? user.MfaEmailEnabled : !user.MfaEmailEnabled))
            throw new AppException("AUTH_CHALLENGE_INVALID");
        if (challenge.Purpose == ChallengePurpose.LoginMfa)
        {
            if (ticket.SessionId is not null || actor is not null) throw new AppException("AUTH_CHALLENGE_INVALID");
        }
        else
        {
            if (actor is null || actor.UserId != user.Id || actor.SessionId != ticket.SessionId) throw new AppException("AUTH_CHALLENGE_INVALID");
            await ActorSession(db, user, actor, now, ct);
        }
        return challenge;
    }
    private async Task CheckSendBudget(AuthDbContext db, Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        // One durable MfaChallenged event per staged email, including every resend.
        // Count send time, not challenge creation: old challenges may have recent resends.
        var sent = await db.AuditEvents.CountAsync(a => a.SubjectId == userId && a.EventType == AuditEventType.MfaChallenged
            && a.OccurredAt > now.AddSeconds(-Limits.ChallengeLifetimeSeconds), ct);
        if (sent >= Limits.MaxSends) throw new AppException("RATE_LIMITED");
    }
    private void StageEmail(AuthDbContext db, User user, AuthChallenge challenge, string trace, DateTimeOffset now, string? nextCode = null)
    {
        var code = nextCode ?? EmailOtpSecret.Create();
        challenge.SecretDigest = secrets.Digest(challenge, code);
        var id = Audit(db, AuditEventType.MfaChallenged, user.Id, trace, AuditOutcome.Pending);
        db.OutboxMessages.Add(new OutboxMessage { EventId = id, Kind = OutboxKind.MfaEmail, CreatedAt = now, NextAttemptAt = now,
            ProtectedPayload = emails.Protect(new(challenge.Id, user.Id, user.Email, code, challenge.Version,
                challenge.AuthVersion, challenge.Purpose, challenge.ExpiresAt)) });
    }
    private async Task<SessionGrant> CompleteLogin(AuthDbContext db, User user, AuthChallenge challenge, string trace, DateTimeOffset now, CancellationToken ct)
    {
        var session = new AuthSession { UserId = user.Id, AuthVersion = user.AuthVersion, AuthenticatedAt = challenge.CreatedAt, PrimaryMethod = challenge.PrimaryMethod,
            MfaVerifiedAt = now, CreatedAt = now, LastSeenAt = now, IdleExpiresAt = now.AddSeconds(jwt.Value.SessionIdleLifetimeSeconds),
            AbsoluteExpiresAt = now.AddSeconds(jwt.Value.SessionAbsoluteLifetimeSeconds) };
        var refresh = RefreshTokenSecret.Create();
        db.Sessions.Add(session);
        db.RefreshTokens.Add(new RefreshToken { SessionId = session.Id, TokenHash = RefreshTokenSecret.Digest(refresh)!,
            CreatedAt = now, ExpiresAt = session.IdleExpiresAt });
        db.LoginHistories.Add(new LoginHistory { UserId = user.Id, SessionId = session.Id, Method = challenge.PrimaryMethod,
            Outcome = AuditOutcome.Success, OccurredAt = now, TraceId = trace });
        Audit(db, AuditEventType.LoginCompleted, user.Id, trace, sessionId: session.Id, method: challenge.PrimaryMethod);
        var roles = await (from ur in db.UserRoles join role in db.Roles on ur.RoleId equals role.Id
            where ur.UserId == user.Id orderby role.Name select role.Name).ToArrayAsync(ct);
        var token = tokens.Issue(session); // Failure rolls back OTP/recovery consumption and session creation.
        return new(new(token, "Bearer", jwt.Value.AccessTokenLifetimeSeconds, new(user.Id, user.Email, user.DisplayName, roles)), refresh, session.IdleExpiresAt);
    }
    private async Task<RecoveryCodesResponse> ReplaceRecoveryCodes(AuthDbContext db, Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        await db.RecoveryCodes.Where(c => c.UserId == userId && c.UsedAt == null).ExecuteUpdateAsync(s => s.SetProperty(c => c.UsedAt, now), ct);
        var codes = Enumerable.Range(0, Limits.RecoveryCodeCount).Select(_ => RefreshTokenSecret.Create()).ToArray();
        db.RecoveryCodes.AddRange(codes.Select(code => new RecoveryCode { UserId = userId, CodeHash = RefreshTokenSecret.Digest(code)!, CreatedAt = now }));
        return new(codes);
    }
    private async Task RevokeForChange(AuthDbContext db, User user, string reason, string trace, DateTimeOffset now, CancellationToken ct)
    {
        user.AuthVersion++;
        var sessionIds = await db.Sessions.Where(s => s.UserId == user.Id && s.RevokedAt == null).Select(s => s.Id).ToArrayAsync(ct);
        await db.RefreshTokens.Where(r => db.Sessions.Where(s => s.UserId == user.Id).Select(s => s.Id).Contains(r.SessionId) && r.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.RevokedAt, now), ct);
        await db.Sessions.Where(s => s.UserId == user.Id && s.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(s => s.RevokedAt, now).SetProperty(s => s.RevokeReason, reason), ct);
        var tracked = db.ChangeTracker.Entries<AuthChallenge>().Where(e => e.Entity.UserId == user.Id).Select(e => e.Entity).ToArray();
        var trackedIds = tracked.Select(c => c.Id).ToArray();
        await db.Challenges.Where(c => c.UserId == user.Id && c.ConsumedAt == null && !trackedIds.Contains(c.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.ConsumedAt, now), ct);
        foreach (var challenge in tracked) challenge.ConsumedAt ??= now;
        foreach (var id in sessionIds) Audit(db, AuditEventType.SessionRevoked, user.Id, trace, sessionId: id);
    }
    private Guid Audit(AuthDbContext db, AuditEventType type, Guid userId, string trace, AuditOutcome outcome = AuditOutcome.Success,
        AuditReason reason = AuditReason.None, Guid? sessionId = null, AuthenticationMethod? method = null) =>
        new AuditWriter(db, audits, clock).Stage(new(type, outcome, reason, trace, userId, userId, new(SessionId: sessionId, Method: method)));
    private static Task<User?> LockUser(AuthDbContext db, Guid userId, CancellationToken ct) => db.Users
        .FromSqlInterpolated($"SELECT * FROM [Users] WITH (UPDLOCK, ROWLOCK) WHERE [Id] = {userId}").SingleOrDefaultAsync(ct);
    private static bool Active(User? user, DateTimeOffset now) => user is { Status: UserStatus.Active, EmailConfirmedAt: not null }
        && !(user.LockoutEnd > now);
    private static async Task<AuthSession> ActorSession(AuthDbContext db, User user, PasswordActor actor, DateTimeOffset now, CancellationToken ct)
    {
        var session = await db.Sessions.SingleOrDefaultAsync(s => s.Id == actor.SessionId && s.UserId == user.Id, ct);
        if (actor.UserId != user.Id || actor.AuthVersion != user.AuthVersion || session is null || session.AuthVersion != user.AuthVersion
            || session.RevokedAt != null || session.IdleExpiresAt <= now || session.AbsoluteExpiresAt <= now
            || (user.MfaEmailEnabled && session.MfaVerifiedAt is null)) throw new AppException("AUTH_SESSION_INVALID");
        return session;
    }
    private static MfaChallengeGrant Grant(AuthChallenge c, Guid? sessionId) => new(new(c.Id, c.Purpose.ToString(), c.ExpiresAt, c.NextSendAt),
        new(c.Id, c.UserId, c.ContextId, c.Purpose, c.AuthVersion, sessionId));
}
