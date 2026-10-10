using AuthProject.Configuration;
using AuthProject.Data;
using AuthProject.Data.Configurations;
using AuthProject.Entities;
using AuthProject.Exceptions;
using AuthProject.Logging;
using AuthProject.Security;
using AuthProject.Security.Interfaces;
using AuthProject.Services;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AuthProject.Repositories;

public sealed record GoogleCompletionGrant(GoogleBinding Binding, string Ticket);
public sealed class GoogleStore(IDbContextFactory<AuthDbContext> factory, IOptions<JwtOptions> jwt,
    IOptions<EmailOtpOptions> otp, MfaStore mfa, IAccessTokenIssuer tokens, IAuditPayloadProtector audits, TimeProvider clock)
{
    public async Task<GoogleCompletionGrant> StageAsync(GoogleIdentity identity, GoogleBinding binding, string trace, CancellationToken ct)
    {
        try { return await StageCore(identity, binding, trace, ct); }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            // Retry only unique-key account creation after rollback; no transaction/token replay.
            try { return await StageCore(identity, binding, trace, ct); }
            catch (DbUpdateException retry) when (retry.InnerException is SqlException { Number: 2601 or 2627 })
            { throw new AppException("AUTH_GOOGLE_LINK_REQUIRED"); }
        }
    }
    private async Task<GoogleCompletionGrant> StageCore(GoogleIdentity identity, GoogleBinding binding, string trace, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = clock.GetUtcNow();
        var primaryAt = now;
        if (binding.StartedAt > now || binding.StartedAt <= now.AddMinutes(-10)) throw new AppException("AUTH_GOOGLE_INVALID");
        var external = await db.ExternalLogins.AsNoTracking().SingleOrDefaultAsync(x => x.Provider == "Google" && x.ProviderSubject == identity.Subject, ct);
        User user;
        if (binding.Flow != GoogleFlow.Login)
        {
            if (binding.Actor is null) throw new AppException("AUTH_GOOGLE_INVALID");
            user = await LockUser(db, binding.Actor.UserId, ct) ?? throw new AppException("AUTH_SESSION_INVALID");
            await ActorSession(db, user, binding.Actor, binding.Flow == GoogleFlow.Link, ct);
            if (binding.Flow == GoogleFlow.Link && external is not null && external.UserId != user.Id) throw new AppException("RESOURCE_CONFLICT");
            if (binding.Flow == GoogleFlow.Reauthenticate)
            {
                now = clock.GetUtcNow();
                if (external?.UserId != user.Id || identity.AuthenticationTime is not { } time
                    || time < binding.StartedAt.AddSeconds(-30).ToUnixTimeSeconds()
                    || time <= now.AddSeconds(-otp.Value.RecentAuthenticationSeconds).ToUnixTimeSeconds()
                    || time > now.AddSeconds(30).ToUnixTimeSeconds()) throw new AppException("AUTH_GOOGLE_INVALID");
                primaryAt = DateTimeOffset.FromUnixTimeSeconds(time);
                if (primaryAt > now) primaryAt = now;
            }
        }
        else if (external is not null) user = await LockUser(db, external.UserId, ct) ?? throw new AppException("AUTH_GOOGLE_INVALID");
        else
        {
            var normalized = EmailNormalizer.Normalize(identity.Email);
            if (await db.Users.AnyAsync(u => u.NormalizedEmail == normalized, ct)) throw new AppException("AUTH_GOOGLE_LINK_REQUIRED");
            user = new User { Email = identity.Email, DisplayName = identity.DisplayName, Status = UserStatus.Active, EmailConfirmedAt = now };
            db.Users.Add(user); db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = SystemRoles.UserId });
            db.ExternalLogins.Add(new ExternalLogin { UserId = user.Id, Provider = "Google", ProviderSubject = identity.Subject, CreatedAt = now });
            Audit(db, AuditEventType.Registered, user.Id, trace); Audit(db, AuditEventType.GoogleLinked, user.Id, trace);
        }
        if (!Active(user)) throw new AppException("AUTH_INVALID_CREDENTIALS");
        var ticket = RefreshTokenSecret.Create();
        var challenge = new AuthChallenge { UserId = user.Id, Purpose = ChallengePurpose.GoogleCompletion, PrimaryMethod = AuthenticationMethod.Google,
            SecretDigest = RefreshTokenSecret.Digest(ticket)!, ContextId = binding.ContextId, AuthVersion = user.AuthVersion,
            CreatedAt = primaryAt, ExpiresAt = clock.GetUtcNow().AddSeconds(60) };
        db.Challenges.Add(challenge);
        db.LoginHistories.Add(new LoginHistory { UserId = user.Id, Method = AuthenticationMethod.Google, Outcome = AuditOutcome.Pending,
            OccurredAt = clock.GetUtcNow(), TraceId = trace });
        Audit(db, AuditEventType.GoogleAuthenticated, user.Id, trace, AuditOutcome.Pending);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return new(binding with { ChallengeId = challenge.Id, Subject = identity.Subject, UserId = user.Id }, ticket);
    }
    public async Task AuthorizeActorAsync(PasswordActor actor, bool recent, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var user = await LockUser(db, actor.UserId, ct) ?? throw new AppException("AUTH_SESSION_INVALID");
        await ActorSession(db, user, actor, recent, ct);
    }
    public async Task<MfaLoginResult> CompleteAsync(GoogleBinding binding, string ticket, PasswordActor? actor, string trace, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var user = await LockUser(db, binding.UserId ?? Guid.Empty, ct);
        var now = clock.GetUtcNow(); var digest = RefreshTokenSecret.Digest(ticket);
        var challenge = await db.Challenges.SingleOrDefaultAsync(c => c.Id == binding.ChallengeId, ct);
        if (user is null || !Active(user) || digest is null || challenge is null || challenge.UserId != user.Id
            || challenge.Purpose != ChallengePurpose.GoogleCompletion || challenge.PrimaryMethod != AuthenticationMethod.Google
            || challenge.ContextId != binding.ContextId || challenge.AuthVersion != user.AuthVersion || challenge.SecretDigest != digest
            || challenge.ConsumedAt != null || challenge.ExpiresAt <= now || challenge.CreatedAt > now) throw new AppException("AUTH_CHALLENGE_INVALID");
        challenge.ConsumedAt = now;
        MfaLoginResult result;
        if (binding.Flow == GoogleFlow.Login)
        {
            if (binding.Actor is not null || actor is not null || !await db.ExternalLogins.AnyAsync(x => x.UserId == user.Id
                && x.Provider == "Google" && x.ProviderSubject == binding.Subject, ct)) throw new AppException("AUTH_CHALLENGE_INVALID");
            result = user.MfaEmailEnabled
                ? new(Challenge: await mfa.StageGoogleAsync(db, user, challenge.CreatedAt, null, trace, ct))
                : new(Session: await Session(db, user, challenge.CreatedAt, trace, ct));
        }
        else
        {
            if (binding.Actor is null || actor != binding.Actor) throw new AppException("AUTH_CHALLENGE_INVALID");
            var session = await ActorSession(db, user, actor, binding.Flow == GoogleFlow.Link, ct);
            if (binding.Flow == GoogleFlow.Link)
            {
                var existing = await db.ExternalLogins.SingleOrDefaultAsync(x => x.Provider == "Google" && x.ProviderSubject == binding.Subject, ct);
                if (existing is not null && existing.UserId != user.Id) throw new AppException("RESOURCE_CONFLICT");
                if (existing is null)
                {
                    if (await db.ExternalLogins.AnyAsync(x => x.Provider == "Google" && x.UserId == user.Id, ct)) throw new AppException("RESOURCE_CONFLICT");
                    db.ExternalLogins.Add(new ExternalLogin { UserId = user.Id, Provider = "Google", ProviderSubject = binding.Subject!, CreatedAt = now });
                    await Revoke(db, user, challenge.Id, "GoogleLinked", trace, ct); Audit(db, AuditEventType.GoogleLinked, user.Id, trace);
                }
                result = new();
            }
            else
            {
                if (!await db.ExternalLogins.AnyAsync(x => x.UserId == user.Id && x.Provider == "Google" && x.ProviderSubject == binding.Subject, ct))
                    throw new AppException("AUTH_CHALLENGE_INVALID");
                if (user.MfaEmailEnabled) result = new(Challenge: await mfa.StageGoogleAsync(db, user, challenge.CreatedAt, actor, trace, ct));
                else
                {
                    session.AuthenticatedAt = challenge.CreatedAt; session.PrimaryMethod = AuthenticationMethod.Google;
                    Audit(db, AuditEventType.Reauthenticated, user.Id, trace); result = new();
                }
            }
        }
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return result;
    }
    public async Task UnlinkAsync(PasswordActor actor, string trace, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var user = await LockUser(db, actor.UserId, ct) ?? throw new AppException("AUTH_SESSION_INVALID");
        await ActorSession(db, user, actor, true, ct);
        var external = await db.ExternalLogins.SingleOrDefaultAsync(x => x.UserId == user.Id && x.Provider == "Google", ct);
        if (external is null) throw new AppException("RESOURCE_NOT_FOUND");
        if (user.PasswordHash is null && await db.ExternalLogins.CountAsync(x => x.UserId == user.Id, ct) <= 1) throw new AppException("AUTH_LAST_LOGIN_METHOD");
        db.ExternalLogins.Remove(external); await Revoke(db, user, Guid.Empty, "GoogleUnlinked", trace, ct);
        Audit(db, AuditEventType.GoogleUnlinked, user.Id, trace); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }
    private async Task<SessionGrant> Session(AuthDbContext db, User user, DateTimeOffset primaryAt, string trace, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var session = new AuthSession { UserId = user.Id, AuthVersion = user.AuthVersion, PrimaryMethod = AuthenticationMethod.Google,
            AuthenticatedAt = primaryAt, CreatedAt = now, LastSeenAt = now, IdleExpiresAt = now.AddSeconds(jwt.Value.SessionIdleLifetimeSeconds),
            AbsoluteExpiresAt = now.AddSeconds(jwt.Value.SessionAbsoluteLifetimeSeconds) };
        db.Sessions.Add(session); var refresh = RefreshTokenSecret.Create();
        db.RefreshTokens.Add(new RefreshToken { SessionId = session.Id, TokenHash = RefreshTokenSecret.Digest(refresh)!, CreatedAt = now, ExpiresAt = session.IdleExpiresAt });
        db.LoginHistories.Add(new LoginHistory { UserId = user.Id, SessionId = session.Id, Method = AuthenticationMethod.Google,
            Outcome = AuditOutcome.Success, OccurredAt = now, TraceId = trace }); Audit(db, AuditEventType.LoginCompleted, user.Id, trace);
        var roles = await (from ur in db.UserRoles join r in db.Roles on ur.RoleId equals r.Id where ur.UserId == user.Id orderby r.Name select r.Name).ToArrayAsync(ct);
        return new(new(tokens.Issue(session), "Bearer", jwt.Value.AccessTokenLifetimeSeconds, new(user.Id, user.Email, user.DisplayName, roles)), refresh, session.IdleExpiresAt);
    }
    private async Task Revoke(AuthDbContext db, User user, Guid currentChallenge, string reason, string trace, CancellationToken ct)
    {
        var now = clock.GetUtcNow(); user.AuthVersion++;
        var ids = await db.Sessions.Where(s => s.UserId == user.Id && s.RevokedAt == null).Select(s => s.Id).ToArrayAsync(ct);
        await db.RefreshTokens.Where(r => db.Sessions.Where(s => s.UserId == user.Id).Select(s => s.Id).Contains(r.SessionId) && r.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.RevokedAt, now), ct);
        await db.Sessions.Where(s => s.UserId == user.Id && s.RevokedAt == null).ExecuteUpdateAsync(s => s.SetProperty(s => s.RevokedAt, now).SetProperty(s => s.RevokeReason, reason), ct);
        await db.Challenges.Where(c => c.UserId == user.Id && c.ConsumedAt == null && c.Id != currentChallenge).ExecuteUpdateAsync(s => s.SetProperty(c => c.ConsumedAt, now), ct);
        foreach (var id in ids) new AuditWriter(db, audits, clock).Stage(new(AuditEventType.SessionRevoked, AuditOutcome.Success, AuditReason.None, trace, user.Id, user.Id, new(SessionId: id)));
    }
    private bool Active(User user) => user.Status == UserStatus.Active && user.EmailConfirmedAt is not null && !(user.LockoutEnd > clock.GetUtcNow());
    private async Task<AuthSession> ActorSession(AuthDbContext db, User user, PasswordActor actor, bool recent, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var session = await db.Sessions.SingleOrDefaultAsync(s => s.Id == actor.SessionId && s.UserId == user.Id, ct);
        if (!Active(user) || actor.AuthVersion != user.AuthVersion || session is null || session.AuthVersion != user.AuthVersion
            || session.RevokedAt != null || session.IdleExpiresAt <= now || session.AbsoluteExpiresAt <= now
            || user.MfaEmailEnabled && session.MfaVerifiedAt is null) throw new AppException("AUTH_SESSION_INVALID");
        if (recent && (!RecentAuthenticationRules.Primary(session, now, otp.Value.RecentAuthenticationSeconds)
            || user.MfaEmailEnabled && !RecentAuthenticationRules.Mfa(session, now, otp.Value.RecentAuthenticationSeconds))) throw new AppException("AUTH_STEP_UP_REQUIRED");
        return session;
    }
    private static Task<User?> LockUser(AuthDbContext db, Guid user, CancellationToken ct) => db.Users
        .FromSqlInterpolated($"SELECT * FROM [Users] WITH (UPDLOCK, ROWLOCK) WHERE [Id] = {user}").SingleOrDefaultAsync(ct);
    private void Audit(AuthDbContext db, AuditEventType type, Guid user, string trace, AuditOutcome outcome = AuditOutcome.Success) =>
        new AuditWriter(db, audits, clock).Stage(new(type, outcome, AuditReason.None, trace, user, user, new(Method: AuthenticationMethod.Google)));
}
