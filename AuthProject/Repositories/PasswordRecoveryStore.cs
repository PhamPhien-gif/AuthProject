using AuthProject.Configuration;
using AuthProject.Data;
using AuthProject.Entities;
using AuthProject.Logging;
using AuthProject.Repositories.Interfaces;
using AuthProject.Security;
using AuthProject.Security.Interfaces;
using AuthProject.Services.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AuthProject.Repositories;

public sealed class PasswordRecoveryStore(IDbContextFactory<AuthDbContext> factory, IOptions<PasswordRecoveryOptions> options,
    IPasswordHashService passwords, IPasswordEmailProtector emails, IAuditPayloadProtector audits, TimeProvider clock) : IPasswordRecoveryStore
{
    public async Task ForgotAsync(string email, string trace, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var normalized = EmailNormalizer.Normalize(email);
        var user = await db.Users.FromSqlInterpolated($"SELECT * FROM [Users] WITH (UPDLOCK, ROWLOCK) WHERE [NormalizedEmail] = {normalized}").SingleOrDefaultAsync(ct);
        if (!Eligible(user)) return;
        var now = clock.GetUtcNow();
        var recent = await db.Challenges.AsNoTracking().Where(c => c.UserId == user!.Id && c.Purpose == ChallengePurpose.ResetPassword)
            .OrderByDescending(c => c.CreatedAt).ThenBy(c => c.Id).FirstOrDefaultAsync(ct);
        if (recent?.NextSendAt > now || await db.Challenges.CountAsync(c => c.UserId == user!.Id && c.Purpose == ChallengePurpose.ResetPassword
            && c.CreatedAt > now.AddSeconds(-options.Value.SendWindowSeconds), ct) >= options.Value.MaxSendsPerWindow) return;
        await db.Challenges.Where(c => c.UserId == user!.Id && c.Purpose == ChallengePurpose.ResetPassword && c.ConsumedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.ConsumedAt, now), ct);
        var token = EmailVerificationToken.Create(); // Shared 32-byte opaque token primitive; purpose is enforced in SQL.
        var challenge = new AuthChallenge { UserId = user!.Id, Purpose = ChallengePurpose.ResetPassword,
            SecretDigest = EmailVerificationToken.Digest(token)!, ContextId = Guid.NewGuid(), AuthVersion = user.AuthVersion,
            CreatedAt = now, ExpiresAt = now.AddSeconds(options.Value.TokenLifetimeSeconds), SendCount = 1,
            NextSendAt = now.AddSeconds(options.Value.ResendCooldownSeconds) };
        db.Challenges.Add(challenge);
        StageMail(db, challenge.Id, new(PasswordEmailPurpose.ResetLink, user.Id, user.Email, user.AuthVersion, now, challenge.Id, token, challenge.ExpiresAt));
        new AuditWriter(db, audits, clock).Stage(new(AuditEventType.PasswordResetRequested, AuditOutcome.Pending, AuditReason.None, trace, SubjectId: user.Id));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }
    public async Task<bool> ResetAsync(string digest, string newPassword, string trace, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var snapshot = await db.Challenges.AsNoTracking().SingleOrDefaultAsync(c => c.SecretDigest == digest && c.Purpose == ChallengePurpose.ResetPassword, ct);
        if (snapshot is null) return false;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var user = await LockUser(db, snapshot.UserId, ct);
        var challenge = await db.Challenges.SingleOrDefaultAsync(c => c.Id == snapshot.Id, ct);
        var now = clock.GetUtcNow();
        if (!Eligible(user) || challenge is null || challenge.UserId != user!.Id || challenge.Purpose != ChallengePurpose.ResetPassword
            || challenge.ConsumedAt != null || challenge.ExpiresAt <= now || challenge.AuthVersion != user.AuthVersion) return false;
        user.PasswordHash = passwords.Hash(user, newPassword);
        user.AuthVersion++;
        user.FailedLoginCount = 0; user.FailedLoginWindowStart = null; user.LockoutEnd = null;
        await Invalidate(db, user, now, "PasswordReset", trace, ct);
        // Bulk invalidation includes this challenge; detach its old rowversion to avoid stale updates.
        db.Entry(challenge).State = EntityState.Detached;
        StageCompletion(db, user, AuditEventType.PasswordReset, trace, now);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return true;
    }
    public async Task<string?> GetActorEmailAsync(PasswordActor actor, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var now = clock.GetUtcNow();
        return await (from s in db.Sessions.AsNoTracking() join u in db.Users.AsNoTracking() on s.UserId equals u.Id
            where s.Id == actor.SessionId && u.Id == actor.UserId && s.AuthVersion == actor.AuthVersion && u.AuthVersion == actor.AuthVersion
                && s.RevokedAt == null && s.IdleExpiresAt > now && s.AbsoluteExpiresAt > now && u.Status == UserStatus.Active
                && u.EmailConfirmedAt != null && u.PasswordHash != null && (!u.MfaEmailEnabled || s.MfaVerifiedAt != null)
            select u.Email).SingleOrDefaultAsync(ct);
    }
    public Task<PasswordMutationResult> ChangeAsync(PasswordActor actor, PasswordAcceptance accepted, string newPassword, string trace, CancellationToken ct)
        => Mutate(actor, accepted, newPassword, trace, ct);
    public Task<PasswordMutationResult> ReauthenticateAsync(PasswordActor actor, PasswordAcceptance accepted, string trace, CancellationToken ct)
        => Mutate(actor, accepted, null, trace, ct);
    private async Task<PasswordMutationResult> Mutate(PasswordActor actor, PasswordAcceptance accepted, string? newPassword, string trace, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var user = await LockUser(db, actor.UserId, ct);
        var session = await db.Sessions.FromSqlInterpolated($"SELECT * FROM [AuthSessions] WITH (UPDLOCK, ROWLOCK) WHERE [Id] = {actor.SessionId} AND [UserId] = {actor.UserId}")
            .SingleOrDefaultAsync(ct);
        var now = clock.GetUtcNow();
        if (!Eligible(user) || session is null || accepted.UserId != actor.UserId || user!.AuthVersion != actor.AuthVersion
            || accepted.AuthVersion != user.AuthVersion || session.AuthVersion != user.AuthVersion || session.RevokedAt != null
            || session.IdleExpiresAt <= now || session.AbsoluteExpiresAt <= now || user.LockoutEnd > now) return PasswordMutationResult.InvalidSession;
        if (newPassword is not null && !RecentAuthenticationRules.Primary(session, now, options.Value.RecentAuthenticationSeconds)
            || user.MfaEmailEnabled && !RecentAuthenticationRules.Mfa(session, now, options.Value.RecentAuthenticationSeconds))
            return PasswordMutationResult.StepUpRequired;
        if (newPassword is null)
        {
            // A password proof never renews MFA proof, idle expiry, absolute expiry, or AuthVersion.
            session.AuthenticatedAt = now;
            new AuditWriter(db, audits, clock).Stage(new(AuditEventType.Reauthenticated, AuditOutcome.Success, AuditReason.None,
                trace, user.Id, user.Id, new(SessionId: session.Id, Method: AuthenticationMethod.Password)));
        }
        else
        {
            user.PasswordHash = passwords.Hash(user, newPassword);
            user.AuthVersion++;
            await Invalidate(db, user, now, "PasswordChanged", trace, ct);
            // The session was revoked through ExecuteUpdate in Invalidate, not through this tracked copy.
            db.Entry(session).State = EntityState.Detached;
            StageCompletion(db, user, AuditEventType.PasswordChanged, trace, now);
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return PasswordMutationResult.Success;
    }
    private async Task Invalidate(AuthDbContext db, User user, DateTimeOffset now, string reason, string trace, CancellationToken ct)
    {
        // User lock serializes with P07 login/refresh/logout, then revoke all families and outstanding challenges.
        var ids = db.Sessions.Where(s => s.UserId == user.Id).Select(s => s.Id);
        var active = await db.Sessions.AsNoTracking().Where(s => s.UserId == user.Id && s.RevokedAt == null).Select(s => s.Id).ToArrayAsync(ct);
        await db.RefreshTokens.Where(t => ids.Contains(t.SessionId) && t.RevokedAt == null).ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
        await db.Sessions.Where(s => s.UserId == user.Id && s.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, now).SetProperty(x => x.RevokeReason, reason), ct);
        await db.Challenges.Where(c => c.UserId == user.Id && c.ConsumedAt == null).ExecuteUpdateAsync(s => s.SetProperty(c => c.ConsumedAt, now), ct);
        foreach (var id in active) new AuditWriter(db, audits, clock).Stage(new(AuditEventType.SessionRevoked, AuditOutcome.Success, AuditReason.Revoked,
            trace, user.Id, user.Id, new(SessionId: id)));
    }
    private void StageCompletion(AuthDbContext db, User user, AuditEventType type, string trace, DateTimeOffset now)
    {
        var eventId = new AuditWriter(db, audits, clock).Stage(new(type, AuditOutcome.Success, AuditReason.None, trace, user.Id, user.Id));
        StageMail(db, eventId, new(PasswordEmailPurpose.PasswordChanged, user.Id, user.Email, user.AuthVersion, now));
    }
    private void StageMail(AuthDbContext db, Guid eventId, PasswordEmail email) => db.OutboxMessages.Add(new OutboxMessage
    {
        EventId = eventId, Kind = OutboxKind.PasswordEmail, ProtectedPayload = emails.Protect(email), CreatedAt = clock.GetUtcNow(), NextAttemptAt = clock.GetUtcNow()
    });
    private static bool Eligible(User? user) => user is { Status: UserStatus.Active, EmailConfirmedAt: not null, PasswordHash: not null };
    private static Task<User?> LockUser(AuthDbContext db, Guid id, CancellationToken ct) =>
        db.Users.FromSqlInterpolated($"SELECT * FROM [Users] WITH (UPDLOCK, ROWLOCK) WHERE [Id] = {id}").SingleOrDefaultAsync(ct);
}
