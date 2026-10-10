using AuthProject.Configuration;
using AuthProject.Data;
using AuthProject.Entities;
using AuthProject.Logging;
using AuthProject.Security;
using AuthProject.Security.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AuthProject.Repositories;

public sealed class PasswordAttemptStore(IDbContextFactory<AuthDbContext> factory, IOptions<LockoutOptions> options,
    IAuditPayloadProtector protector, TimeProvider clock, IHttpContextAccessor? http = null) : IPasswordAttemptStore
{
    public async Task<User?> FindAsync(string email, CancellationToken cancellationToken = default)
    {
        await using var context = await factory.CreateDbContextAsync(cancellationToken);
        var normalized = EmailNormalizer.Normalize(email);
        return await context.Users.AsNoTracking().SingleOrDefaultAsync(x => x.NormalizedEmail == normalized, cancellationToken);
    }

    public async Task<PasswordAcceptance?> CompleteAsync(User? snapshot, PasswordCheck check, string traceId, CancellationToken cancellationToken = default)
    {
        await using var context = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        User? user = null;
        if (snapshot is not null)
            user = await context.Users.FromSqlInterpolated($"SELECT * FROM [Users] WITH (UPDLOCK, ROWLOCK) WHERE [Id] = {snapshot.Id}")
                .SingleOrDefaultAsync(cancellationToken);
        var now = clock.GetUtcNow();
        var decision = new PasswordAttemptDecision(false, AuditReason.InvalidCredentials);
        var previouslyLocked = user?.LockoutEnd > now;
        if (user is not null)
        {
            var fresh = snapshot!.AuthVersion == user.AuthVersion && string.Equals(snapshot.PasswordHash, user.PasswordHash, StringComparison.Ordinal);
            if (fresh)
                decision = LockoutRules.Apply(user, user.PasswordHash is not null && check != PasswordCheck.Failed, now, options.Value);
            // Stale verification never authenticates or increments a newly changed credential's counter.
        }
        var writer = new AuditWriter(context, protector, clock);
        writer.Stage(new(decision.Accepted ? AuditEventType.PasswordVerified : AuditEventType.LoginFailed,
            decision.Accepted ? AuditOutcome.Pending : AuditOutcome.Failure, decision.Reason, traceId, decision.Accepted ? user?.Id : null, user?.Id,
            new(Method: AuthenticationMethod.Password)));
        if (!decision.Accepted)
        {
            var agent = http?.HttpContext?.Request.Headers.UserAgent.ToString();
            context.LoginHistories.Add(new LoginHistory { UserId = user?.Id, Method = AuthenticationMethod.Password,
                Outcome = AuditOutcome.Failure, ReasonCode = decision.Reason, OccurredAt = now, TraceId = traceId,
                IpAddress = http?.HttpContext?.Connection.RemoteIpAddress?.ToString(), UserAgent = agent is null ? null : agent[..Math.Min(agent.Length, 256)] });
        }
        if (!previouslyLocked && user?.LockoutEnd > now)
            writer.Stage(new(AuditEventType.AccountLocked, AuditOutcome.Success, AuditReason.Locked, traceId, SubjectId: user.Id));
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return decision.Accepted ? new(user!.Id, user.AuthVersion, check == PasswordCheck.NeedsRehash) : null;
    }
}
