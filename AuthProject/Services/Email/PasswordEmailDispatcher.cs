using AuthProject.Configuration;
using AuthProject.Data;
using AuthProject.Entities;
using AuthProject.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AuthProject.Services.Email;

public sealed class PasswordEmailDispatcher(IDbContextFactory<AuthDbContext> factory, IPasswordEmailProtector protector,
    IEmailTransport transport, IOptions<PasswordRecoveryOptions> recovery, IOptions<RegistrationOptions> delivery,
    TimeProvider clock, ILogger<PasswordEmailDispatcher> logger)
{
    public async Task<bool> DispatchOneAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var now = clock.GetUtcNow();
        var candidate = await db.OutboxMessages.AsNoTracking().Where(m => m.Kind == OutboxKind.PasswordEmail && m.ProcessedAt == null
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
            var valid = user is not null && user.EmailConfirmedAt != null && user.Email == email.Recipient && email.AuthVersion >= 1;
            if (valid && email.Purpose == PasswordEmailPurpose.ResetLink)
            {
                var challenge = await db.Challenges.AsNoTracking().SingleOrDefaultAsync(c => c.Id == email.ChallengeId, ct);
                valid = user!.Status == UserStatus.Active && user.PasswordHash != null && challenge is not null
                    && challenge.UserId == user.Id && challenge.Purpose == ChallengePurpose.ResetPassword && challenge.ConsumedAt == null
                    && challenge.ExpiresAt > clock.GetUtcNow() && challenge.ExpiresAt == email.ExpiresAt && challenge.AuthVersion == user.AuthVersion
                    && email.AuthVersion == user.AuthVersion && EmailVerificationToken.Digest(email.Token) == challenge.SecretDigest && claimed.EventId == challenge.Id;
            }
            else if (valid && email.Purpose == PasswordEmailPurpose.PasswordChanged)
                valid = email.Token is null && email.ChallengeId is null && email.ExpiresAt is null && email.AuthVersion <= user!.AuthVersion
                    && await db.AuditEvents.AnyAsync(a => a.Id == claimed.EventId && a.SubjectId == user.Id
                        && (a.EventType == AuditEventType.PasswordChanged || a.EventType == AuditEventType.PasswordReset), ct);
            else valid = false;
            if (valid)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(delivery.Value.SmtpTimeoutSeconds));
                await transport.SendAsync(claimed.Id, email.Recipient, PasswordEmailTemplate.Render(email, recovery.Value.ResetUrl), timeout.Token);
            }
            await db.OutboxMessages.Where(m => m.Id == claimed.Id && m.RowVersion == claimed.RowVersion)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.ProcessedAt, clock.GetUtcNow()).SetProperty(m => m.LeaseUntil, (DateTimeOffset?)null), ct);
            await tx.CommitAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning("Password email outbox {MessageId} failed ({FailureType}).", claimed.Id, ex.GetType().Name);
            var next = clock.GetUtcNow().AddSeconds(Math.Min(900, 30 * Math.Pow(2, Math.Min(claimed.Attempts - 1, 5))));
            await db.OutboxMessages.Where(m => m.Id == claimed.Id && m.RowVersion == claimed.RowVersion)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.NextAttemptAt, next).SetProperty(m => m.LeaseUntil, (DateTimeOffset?)null), ct);
        }
        return true;
    }
}
