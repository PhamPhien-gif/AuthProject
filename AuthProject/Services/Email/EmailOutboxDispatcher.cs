using AuthProject.Configuration;
using AuthProject.Data;
using AuthProject.Entities;
using AuthProject.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AuthProject.Services.Email;

public sealed class EmailOutboxDispatcher(IDbContextFactory<AuthDbContext> factory, IEmailPayloadProtector protector,
    IEmailTransport transport, IOptions<RegistrationOptions> options, TimeProvider clock, ILogger<EmailOutboxDispatcher> logger)
{
    public async Task<bool> DispatchOneAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var now = clock.GetUtcNow();
        var candidate = await db.OutboxMessages.AsNoTracking().Where(x => x.Kind == OutboxKind.Email && x.ProcessedAt == null
                && x.Attempts < 10 && x.NextAttemptAt <= now && (x.LeaseUntil == null || x.LeaseUntil <= now))
            .OrderBy(x => x.NextAttemptAt).ThenBy(x => x.Id).FirstOrDefaultAsync(cancellationToken);
        if (candidate is null) return false;
        var leaseEnd = now.AddSeconds(options.Value.LeaseSeconds);
        OutboxMessage claimed;
        await using (var claimTransaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            var acquired = await db.OutboxMessages.Where(x => x.Id == candidate.Id && x.RowVersion == candidate.RowVersion)
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.LeaseUntil, leaseEnd).SetProperty(x => x.Attempts, x => x.Attempts + 1), cancellationToken);
            if (acquired == 0) return true;
            claimed = await db.OutboxMessages.AsNoTracking().SingleAsync(x => x.Id == candidate.Id, cancellationToken);
            await claimTransaction.CommitAsync(cancellationToken);
        }
        try
        {
            var email = protector.Unprotect(claimed.ProtectedPayload);
            // Same user lock/order as resend/verify: stale messages cannot race those state transitions while sending.
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var user = await db.Users.FromSqlInterpolated($"SELECT * FROM [Users] WITH (UPDLOCK, ROWLOCK) WHERE [Id] = {email.UserId}")
                .SingleOrDefaultAsync(cancellationToken);
            var current = await db.OutboxMessages.AsNoTracking().SingleAsync(x => x.Id == claimed.Id, cancellationToken);
            if (!current.RowVersion.SequenceEqual(claimed.RowVersion) || current.LeaseUntil <= clock.GetUtcNow()) return true;
            var challenge = await db.Challenges.AsNoTracking().SingleOrDefaultAsync(x => x.Id == email.ChallengeId, cancellationToken);
            var valid = user is { Status: UserStatus.PendingVerification, EmailConfirmedAt: null } && challenge is not null
                && challenge.UserId == user.Id && challenge.Purpose == ChallengePurpose.VerifyEmail && challenge.ConsumedAt is null
                && challenge.ExpiresAt > clock.GetUtcNow() && email.ExpiresAt == challenge.ExpiresAt
                && challenge.AuthVersion == user.AuthVersion && email.Recipient == user.Email
                && EmailVerificationToken.Digest(email.Token) == challenge.SecretDigest && claimed.EventId == challenge.Id;
            if (valid)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(options.Value.SmtpTimeoutSeconds));
                await transport.SendAsync(claimed.Id, email.Recipient, VerificationEmailTemplate.Render(email, options.Value.VerificationUrl), timeout.Token);
            }
            // Invalid/expired/replaced mail is terminal and must never be sent.
            await db.OutboxMessages.Where(x => x.Id == claimed.Id && x.RowVersion == claimed.RowVersion)
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.ProcessedAt, clock.GetUtcNow()).SetProperty(x => x.LeaseUntil, (DateTimeOffset?)null), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // Never include raw SMTP exception, recipient, token, body or protected payload.
            logger.LogWarning("Email outbox {MessageId} failed ({FailureType}).", claimed.Id, ex.GetType().Name);
            var next = clock.GetUtcNow().AddSeconds(Math.Min(900, 30 * Math.Pow(2, Math.Min(claimed.Attempts - 1, 5))));
            await db.OutboxMessages.Where(x => x.Id == claimed.Id && x.RowVersion == claimed.RowVersion)
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.NextAttemptAt, next).SetProperty(x => x.LeaseUntil, (DateTimeOffset?)null), cancellationToken);
        }
        return true;
    }
}
