using AuthProject.Security;
using AuthProject.DTOs;
using AuthProject.Configuration;
using AuthProject.Data;
using AuthProject.Entities;
using AuthProject.Exceptions;
using AuthProject.Logging;
using AuthProject.Repositories.Interfaces;
using AuthProject.Security.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AuthProject.Repositories;

public sealed class LoginStore(IDbContextFactory<AuthDbContext> factory, IOptions<JwtOptions> options, IPasswordHashService passwords,
    IAccessTokenIssuer tokens, IAuditPayloadProtector protector, TimeProvider clock) : ILoginStore
{
    public async Task<SessionGrant?> CompleteAsync(PasswordAcceptance accepted, string password, string traceId, string? ip, string? userAgent, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var user = await db.Users.FromSqlInterpolated($"SELECT * FROM [Users] WITH (UPDLOCK, ROWLOCK) WHERE [Id] = {accepted.UserId}")
            .SingleOrDefaultAsync(cancellationToken);
        var now = clock.GetUtcNow();
        if (user is null || user.AuthVersion != accepted.AuthVersion || user.Status != UserStatus.Active
            || user.EmailConfirmedAt is null || user.PasswordHash is null || user.LockoutEnd > now) return null;
        if (user.MfaEmailEnabled)
        {
            db.LoginHistories.Add(new LoginHistory { UserId = user.Id, Method = AuthenticationMethod.Password,
                Outcome = AuditOutcome.Pending, ReasonCode = AuditReason.None, OccurredAt = now, TraceId = traceId });
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            throw new AppException("AUTH_STEP_UP_REQUIRED"); // Fail closed when the MFA module is disabled or a stale caller skips its gate.
        }
        if (accepted.RequiresRehash)
        {
            if (passwords.Verify(user, user.PasswordHash, password) == PasswordCheck.Failed) return null;
            user.PasswordHash = passwords.Hash(user, password);
        }
        var session = new AuthSession { UserId = user.Id, AuthVersion = user.AuthVersion, AuthenticatedAt = now,
            CreatedAt = now, LastSeenAt = now, IdleExpiresAt = now.AddSeconds(options.Value.SessionIdleLifetimeSeconds),
            AbsoluteExpiresAt = now.AddSeconds(options.Value.SessionAbsoluteLifetimeSeconds) };
        db.Sessions.Add(session);
        var refresh = RefreshTokenSecret.Create();
        var digest = RefreshTokenSecret.Digest(refresh)!;
        db.RefreshTokens.Add(new RefreshToken { SessionId = session.Id, TokenHash = digest, CreatedAt = now, ExpiresAt = session.IdleExpiresAt });
        db.LoginHistories.Add(new LoginHistory { UserId = user.Id, SessionId = session.Id, Method = AuthenticationMethod.Password,
            Outcome = AuditOutcome.Success, ReasonCode = AuditReason.None, OccurredAt = now, TraceId = traceId,
            IpAddress = ip is { Length: <= 45 } ? ip : null, UserAgent = userAgent is null ? null : userAgent[..Math.Min(userAgent.Length, 256)] });
        new AuditWriter(db, protector, clock).Stage(new(AuditEventType.LoginCompleted, AuditOutcome.Success, AuditReason.None,
            traceId, user.Id, user.Id, new(SessionId: session.Id, Method: AuthenticationMethod.Password)));
        var roles = await Roles(db, user.Id).ToArrayAsync(cancellationToken);
        var token = tokens.Issue(session); // Signing failure rolls back everything staged in this transaction.
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(new(token, "Bearer", options.Value.AccessTokenLifetimeSeconds, new(user.Id, user.Email, user.DisplayName, roles)), refresh, session.IdleExpiresAt);
    }

    public async Task<MeResponse?> GetMeAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.Users.AsNoTracking().Where(x => x.Id == userId && x.Status == UserStatus.Active && x.EmailConfirmedAt != null)
            .Select(x => new MeResponse(x.Id, x.Email, x.DisplayName,
                (from ur in db.UserRoles join role in db.Roles on ur.RoleId equals role.Id where ur.UserId == x.Id orderby role.Name select role.Name).ToArray(),
                x.EmailConfirmedAt != null, x.MfaEmailEnabled, x.CreatedAt)).SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<string[]?> ValidateSessionAsync(Guid userId, Guid sessionId, long authVersion, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var now = clock.GetUtcNow();
        var valid = await (from session in db.Sessions.AsNoTracking() join user in db.Users.AsNoTracking() on session.UserId equals user.Id
            where session.Id == sessionId && user.Id == userId && session.AuthVersion == authVersion && user.AuthVersion == authVersion
                && session.RevokedAt == null && session.IdleExpiresAt > now && session.AbsoluteExpiresAt > now
                && user.Status == UserStatus.Active && user.EmailConfirmedAt != null && (!user.MfaEmailEnabled || session.MfaVerifiedAt != null)
            select session.Id).AnyAsync(cancellationToken);
        return valid ? await Roles(db, userId).ToArrayAsync(cancellationToken) : null;
    }

    private static IQueryable<string> Roles(AuthDbContext db, Guid userId) =>
        from ur in db.UserRoles.AsNoTracking() join role in db.Roles.AsNoTracking() on ur.RoleId equals role.Id
        where ur.UserId == userId orderby role.Name select role.Name;
}
