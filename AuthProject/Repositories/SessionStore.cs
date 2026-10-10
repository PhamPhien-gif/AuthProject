using AuthProject.Configuration;
using AuthProject.Data;
using AuthProject.DTOs;
using AuthProject.Entities;
using AuthProject.Logging;
using AuthProject.Security;
using AuthProject.Security.Interfaces;
using AuthProject.Repositories.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AuthProject.Repositories;

public sealed record RefreshAttempt(SessionGrant? Grant = null, int? RetryAfterSeconds = null);

public sealed class SessionStore(IDbContextFactory<AuthDbContext> factory, IOptions<JwtOptions> jwt,
    IAccessTokenIssuer issuer, IAuditPayloadProtector protector, TimeProvider clock,
    IOptions<BruteForceOptions> rateOptions, BoundedRateLimitStore limiter, RateLimitKeyFactory keys) : ISessionStore
{
    public async Task<RefreshAttempt> RefreshAsync(string digest, string trace, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var owner = await (from token in db.RefreshTokens.AsNoTracking() join s in db.Sessions.AsNoTracking() on token.SessionId equals s.Id
            where token.TokenHash == digest select new { s.UserId, SessionId = s.Id }).SingleOrDefaultAsync(ct);
        if (owner is null) return new();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // Every mutator takes the user lock before the session lock, including logout-all and login.
        var user = await LockUser(db, owner.UserId, ct);
        var session = await LockSession(db, owner.SessionId, ct);
        var tokenRow = await db.RefreshTokens.SingleOrDefaultAsync(x => x.TokenHash == digest && x.SessionId == owner.SessionId, ct);
        var now = clock.GetUtcNow();
        if (user is null || session is null || tokenRow is null || session.RevokedAt != null
            || session.IdleExpiresAt <= now || session.AbsoluteExpiresAt <= now
            || session.AuthVersion != user.AuthVersion || user.Status != UserStatus.Active || user.EmailConfirmedAt is null
            || user.MfaEmailEnabled && session.MfaVerifiedAt is null) return new();
        // Retained consumed tokens still detect reuse even when their original idle expiry has elapsed.
        if (tokenRow.ConsumedAt != null)
        {
            await Revoke(db, session, "RefreshReuse", now, trace, ct);
            Audit(db, AuditEventType.RefreshReused, AuditOutcome.Failure, AuditReason.Reused, trace, user.Id, session.Id);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct); // The caller can now return 401 without rolling back revocation.
            return new();
        }
        if (tokenRow.RevokedAt != null || tokenRow.ExpiresAt <= now) return new();
        if (rateOptions.Value.Enabled)
        {
            var quota = rateOptions.Value.Policies["Refresh"];
            if (quota.SessionPermitLimit > 0)
            {
                var decision = limiter.Acquire([new("Refresh:session:" + keys.ForSession(session.Id), quota.SessionPermitLimit, TimeSpan.FromSeconds(quota.WindowSeconds))]);
                if (!decision.Allowed) return new(RetryAfterSeconds: decision.RetryAfterSeconds);
            }
        }
        var idle = now.AddSeconds(jwt.Value.SessionIdleLifetimeSeconds);
        session.IdleExpiresAt = idle < session.AbsoluteExpiresAt ? idle : session.AbsoluteExpiresAt;
        session.LastSeenAt = now;
        var lifetime = SessionLifetimeRules.AccessLifetime(session, now, jwt.Value.AccessTokenLifetimeSeconds);
        if (lifetime <= 0) return new();
        var raw = RefreshTokenSecret.Create();
        var next = new RefreshToken { SessionId = session.Id, TokenHash = RefreshTokenSecret.Digest(raw)!, CreatedAt = now, ExpiresAt = session.IdleExpiresAt };
        var roles = await (from ur in db.UserRoles.AsNoTracking() join role in db.Roles.AsNoTracking() on ur.RoleId equals role.Id
            where ur.UserId == user.Id orderby role.Name select role.Name).ToArrayAsync(ct);
        var access = issuer.Issue(session); // Signing/protection errors leave the old token usable.
        tokenRow.ConsumedAt = now;
        await db.SaveChangesAsync(ct); // Free the filtered unique active-token index inside the transaction.
        db.RefreshTokens.Add(next);
        Audit(db, AuditEventType.RefreshRotated, AuditOutcome.Success, AuditReason.None, trace, user.Id, session.Id);
        await db.SaveChangesAsync(ct); // Insert replacement before assigning the self-referencing FK.
        tokenRow.ReplacedById = next.Id;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new(new(new(access, "Bearer", lifetime, new(user.Id, user.Email, user.DisplayName, roles)), raw, next.ExpiresAt));
    }

    public async Task LogoutAsync(string digest, string trace, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var owner = await (from token in db.RefreshTokens.AsNoTracking() join s in db.Sessions.AsNoTracking() on token.SessionId equals s.Id
            where token.TokenHash == digest select new { s.UserId, SessionId = s.Id }).SingleOrDefaultAsync(ct);
        if (owner is null) return;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockUser(db, owner.UserId, ct);
        var session = await LockSession(db, owner.SessionId, ct);
        // A consumed cookie can still revoke its family during a refresh/logout race.
        if (session is not null) await Revoke(db, session, "Logout", clock.GetUtcNow(), trace, ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    public async Task LogoutAllAsync(Guid actor, string trace, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var user = await LockUser(db, actor, ct);
        if (user is null) return;
        // Invalidate primary-factor acceptances that were obtained before this operation.
        user.AuthVersion++;
        var sessions = await db.Sessions.Where(s => s.UserId == actor && s.RevokedAt == null).OrderBy(s => s.Id).ToArrayAsync(ct);
        foreach (var session in sessions) await Revoke(db, session, "LogoutAll", clock.GetUtcNow(), trace, ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    public async Task<bool> RevokeOwnedAsync(Guid actor, Guid id, string trace, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockUser(db, actor, ct);
        // Filter ownership in SQL before locking; never modify another user's session.
        var session = await db.Sessions.FromSqlInterpolated($"SELECT * FROM [AuthSessions] WITH (UPDLOCK, ROWLOCK) WHERE [Id] = {id} AND [UserId] = {actor}").SingleOrDefaultAsync(ct);
        if (session is null) return false;
        await Revoke(db, session, "OwnerRevoked", clock.GetUtcNow(), trace, ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    public async Task<PagedResponse<SessionResponse>> ListAsync(Guid actor, Guid current, PagingRequest paging, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var query = db.Sessions.AsNoTracking().Where(s => s.UserId == actor);
        var total = await query.CountAsync(ct);
        var offset = (long)(paging.Page - 1) * paging.PageSize;
        var items = offset > int.MaxValue ? [] : await query.OrderByDescending(s => s.CreatedAt).ThenBy(s => s.Id)
            .Skip((int)offset).Take(paging.PageSize)
            .Select(s => new SessionResponse(s.Id, s.CreatedAt, s.LastSeenAt, s.IdleExpiresAt, s.AbsoluteExpiresAt, s.RevokedAt, s.Id == current)).ToArrayAsync(ct);
        return new(items, paging.Page, paging.PageSize, total);
    }

    private static Task<User?> LockUser(AuthDbContext db, Guid id, CancellationToken ct) => db.Users
        .FromSqlInterpolated($"SELECT * FROM [Users] WITH (UPDLOCK, ROWLOCK) WHERE [Id] = {id}").SingleOrDefaultAsync(ct);
    private static Task<AuthSession?> LockSession(AuthDbContext db, Guid id, CancellationToken ct) => db.Sessions
        .FromSqlInterpolated($"SELECT * FROM [AuthSessions] WITH (UPDLOCK, ROWLOCK) WHERE [Id] = {id}").SingleOrDefaultAsync(ct);
    private async Task Revoke(AuthDbContext db, AuthSession session, string reason, DateTimeOffset now, string trace, CancellationToken ct)
    {
        if (session.RevokedAt != null) return;
        session.RevokedAt = now;
        session.RevokeReason = reason;
        await db.RefreshTokens.Where(t => t.SessionId == session.Id && t.RevokedAt == null).ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
        Audit(db, AuditEventType.SessionRevoked, AuditOutcome.Success, reason == "RefreshReuse" ? AuditReason.Reused : AuditReason.Revoked, trace, session.UserId, session.Id);
    }
    private void Audit(AuthDbContext db, AuditEventType type, AuditOutcome outcome, AuditReason reason, string trace, Guid user, Guid session) =>
        new AuditWriter(db, protector, clock).Stage(new(type, outcome, reason, trace, user, user, new(SessionId: session)));
}
