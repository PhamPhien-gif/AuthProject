using AuthProject.Configuration;
using AuthProject.Data;
using AuthProject.Data.Configurations;
using AuthProject.Entities;
using AuthProject.Logging;
using AuthProject.Repositories.Interfaces;
using AuthProject.Security;
using AuthProject.Services.Email;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AuthProject.Repositories;

public sealed class RegistrationStore(IDbContextFactory<AuthDbContext> factory, IOptions<RegistrationOptions> options,
    IEmailPayloadProtector emailProtector, IAuditPayloadProtector auditProtector, TimeProvider clock) : IRegistrationStore
{
    public async Task RegisterAsync(string email, string displayName, string passwordHash, string traceId, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var normalized = EmailNormalizer.Normalize(email);
        var existing = await db.Users.FromSqlInterpolated($"SELECT * FROM [Users] WITH (UPDLOCK, HOLDLOCK) WHERE [NormalizedEmail] = {normalized}")
            .SingleOrDefaultAsync(cancellationToken);
        if (existing is not null) return;
        var user = new User { Email = email, DisplayName = displayName, PasswordHash = passwordHash };
        db.Users.Add(user);
        db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = SystemRoles.UserId });
        StageEmail(db, user);
        new AuditWriter(db, auditProtector, clock).Stage(new(AuditEventType.Registered, AuditOutcome.Pending, AuditReason.None, traceId, SubjectId: user.Id));
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            await transaction.RollbackAsync(cancellationToken);
            await using var check = await factory.CreateDbContextAsync(cancellationToken);
            if (!await check.Users.AnyAsync(x => x.NormalizedEmail == normalized, cancellationToken)) throw;
        }
    }

    public async Task ResendAsync(string email, string traceId, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var normalized = EmailNormalizer.Normalize(email);
        var user = await db.Users.FromSqlInterpolated($"SELECT * FROM [Users] WITH (UPDLOCK, ROWLOCK) WHERE [NormalizedEmail] = {normalized}")
            .SingleOrDefaultAsync(cancellationToken);
        if (user is null || user.Status != UserStatus.PendingVerification || user.EmailConfirmedAt is not null) return;
        var now = clock.GetUtcNow();
        var latest = await db.Challenges.Where(x => x.UserId == user.Id && x.Purpose == ChallengePurpose.VerifyEmail)
            .OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        if (latest?.NextSendAt > now) return;
        var windowStart = now.AddSeconds(-options.Value.SendWindowSeconds);
        if (await db.Challenges.CountAsync(x => x.UserId == user.Id && x.Purpose == ChallengePurpose.VerifyEmail && x.CreatedAt > windowStart, cancellationToken)
            >= options.Value.MaxSendsPerWindow) return;
        await db.Challenges.Where(x => x.UserId == user.Id && x.Purpose == ChallengePurpose.VerifyEmail && x.ConsumedAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(x => x.ConsumedAt, now), cancellationToken);
        if (latest is not null) db.Entry(latest).State = EntityState.Detached;
        StageEmail(db, user);
        new AuditWriter(db, auditProtector, clock).Stage(new(AuditEventType.EmailVerificationRequested, AuditOutcome.Pending, AuditReason.None, traceId, SubjectId: user.Id));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<bool> VerifyAsync(string digest, string traceId, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var snapshot = await db.Challenges.AsNoTracking().FirstOrDefaultAsync(x => x.SecretDigest == digest && x.Purpose == ChallengePurpose.VerifyEmail, cancellationToken);
        if (snapshot is null) return false;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var user = await db.Users.FromSqlInterpolated($"SELECT * FROM [Users] WITH (UPDLOCK, ROWLOCK) WHERE [Id] = {snapshot.UserId}").SingleOrDefaultAsync(cancellationToken);
        var challenge = await db.Challenges.SingleAsync(x => x.Id == snapshot.Id, cancellationToken);
        var now = clock.GetUtcNow();
        if (user is null || user.Status != UserStatus.PendingVerification || user.EmailConfirmedAt is not null
            || challenge.ConsumedAt is not null || challenge.ExpiresAt <= now || challenge.AuthVersion != user.AuthVersion) return false;
        challenge.ConsumedAt = now;
        user.EmailConfirmedAt = now;
        user.Status = UserStatus.Active;
        new AuditWriter(db, auditProtector, clock).Stage(new(AuditEventType.EmailVerified, AuditOutcome.Success, AuditReason.None, traceId, SubjectId: user.Id));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private void StageEmail(AuthDbContext db, User user)
    {
        var now = clock.GetUtcNow();
        var token = EmailVerificationToken.Create();
        var challenge = new AuthChallenge { UserId = user.Id, Purpose = ChallengePurpose.VerifyEmail,
            SecretDigest = EmailVerificationToken.Digest(token)!, ContextId = Guid.NewGuid(), AuthVersion = user.AuthVersion,
            CreatedAt = now, ExpiresAt = now.AddSeconds(options.Value.TokenLifetimeSeconds), SendCount = 1,
            NextSendAt = now.AddSeconds(options.Value.ResendCooldownSeconds) };
        db.Challenges.Add(challenge);
        db.OutboxMessages.Add(new OutboxMessage { EventId = challenge.Id, Kind = OutboxKind.Email, CreatedAt = now, NextAttemptAt = now,
            ProtectedPayload = emailProtector.Protect(new(challenge.Id, user.Id, user.Email, token, challenge.ExpiresAt)) });
    }
}
