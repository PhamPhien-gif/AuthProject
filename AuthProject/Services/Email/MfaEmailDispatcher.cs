using AuthProject.Configuration;
using AuthProject.Data;
using AuthProject.Entities;
using AuthProject.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AuthProject.Services.Email;

public sealed class MfaEmailDispatcher(IDbContextFactory<AuthDbContext> factory, MfaEmailProtector protector,
    IEmailTransport transport, EmailOtpSecret secrets, IOptions<RegistrationOptions> delivery,
    TimeProvider clock, ILogger<MfaEmailDispatcher> logger)
{
    public async Task<bool> DispatchOneAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var now = clock.GetUtcNow();
        var candidate = await db.OutboxMessages.AsNoTracking().Where(m => m.Kind == OutboxKind.MfaEmail && m.ProcessedAt == null
            && m.Attempts < 10 && m.NextAttemptAt <= now && (m.LeaseUntil == null || m.LeaseUntil <= now))
            .OrderBy(m => m.NextAttemptAt).ThenBy(m => m.Id).FirstOrDefaultAsync(ct);
        if (candidate is null) return false;
        OutboxMessage claimed;
        await using (var claim = await db.Database.BeginTransactionAsync(ct))
        {
            if (await db.OutboxMessages.Where(m => m.Id == candidate.Id && m.RowVersion == candidate.RowVersion)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.LeaseUntil, now.AddSeconds(delivery.Value.LeaseSeconds)).SetProperty(m => m.Attempts, m => m.Attempts + 1), ct) == 0) return true;
            claimed = await db.OutboxMessages.AsNoTracking().SingleAsync(m => m.Id == candidate.Id, ct);
            await claim.CommitAsync(ct);
        }
        try
        {
            var email = protector.Unprotect(claimed.ProtectedPayload);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var user = await db.Users.FromSqlInterpolated($"SELECT * FROM [Users] WITH (UPDLOCK, ROWLOCK) WHERE [Id] = {email.UserId}").SingleOrDefaultAsync(ct);
            var current = await db.OutboxMessages.AsNoTracking().SingleAsync(m => m.Id == claimed.Id, ct);
            if (!current.RowVersion.SequenceEqual(claimed.RowVersion) || current.LeaseUntil <= clock.GetUtcNow()) return true;
            var challenge = await db.Challenges.AsNoTracking().SingleOrDefaultAsync(c => c.Id == email.ChallengeId, ct);
            var valid = user is { Status: UserStatus.Active, EmailConfirmedAt: not null } && !(user.LockoutEnd > clock.GetUtcNow())
                && user.Email == email.Recipient && user.AuthVersion == email.AuthVersion
                && challenge is not null && challenge.UserId == user.Id && challenge.ConsumedAt == null
                && challenge.Purpose == email.Purpose && challenge.Purpose is (ChallengePurpose.LoginMfa or ChallengePurpose.EnrollMfa or ChallengePurpose.StepUp)
                && challenge.AuthVersion == user.AuthVersion && challenge.Version == email.Version
                && challenge.ExpiresAt == email.ExpiresAt && challenge.ExpiresAt > clock.GetUtcNow()
                && secrets.Matches(challenge, email.Code, challenge.SecretDigest);
            if (valid)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(delivery.Value.SmtpTimeoutSeconds));
                await transport.SendAsync(claimed.Id, email.Recipient, MfaEmailTemplate.Render(email), timeout.Token);
            }
            await db.OutboxMessages.Where(m => m.Id == claimed.Id && m.RowVersion == claimed.RowVersion)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.ProcessedAt, clock.GetUtcNow()).SetProperty(m => m.LeaseUntil, (DateTimeOffset?)null), ct);
            await tx.CommitAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning("MFA email outbox {MessageId} failed ({FailureType}).", claimed.Id, ex.GetType().Name);
            var next = clock.GetUtcNow().AddSeconds(Math.Min(900, 30 * Math.Pow(2, Math.Min(claimed.Attempts - 1, 5))));
            await db.OutboxMessages.Where(m => m.Id == claimed.Id && m.RowVersion == claimed.RowVersion)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.NextAttemptAt, next).SetProperty(m => m.LeaseUntil, (DateTimeOffset?)null), ct);
        }
        return true;
    }
}

