using AuthProject.Configuration;
using AuthProject.Data;
using AuthProject.Entities;
using AuthProject.Logging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace AuthProject.Services;

public sealed class AuditDispatcher(IDbContextFactory<AuthDbContext> factory, IAuditPayloadProtector protector,
    IAuditSink sink, IOptions<AuditDeliveryOptions> options, TimeProvider clock, ILogger<AuditDispatcher> logger)
{
    public async Task<bool> DispatchOneAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct); var now = clock.GetUtcNow();
        var candidate = await db.OutboxMessages.AsNoTracking().Where(m => m.Kind == OutboxKind.Audit && m.ProcessedAt == null
            && m.DeadLetteredAt == null && m.NextAttemptAt <= now && (m.LeaseUntil == null || m.LeaseUntil <= now))
            .OrderBy(m => m.NextAttemptAt).ThenBy(m => m.Id).FirstOrDefaultAsync(ct);
        if (candidate is null) return false;
        if (candidate.Attempts >= options.Value.MaxAttempts)
        {
            if (await db.OutboxMessages.Where(m => m.Id == candidate.Id && m.RowVersion == candidate.RowVersion)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.DeadLetteredAt, now).SetProperty(m => m.LeaseUntil, (DateTimeOffset?)null)
                    .SetProperty(m => m.LastErrorCode, "LEASE_EXHAUSTED"), ct) == 1)
                Alert(candidate.Id, candidate.EventId, "LEASE_EXHAUSTED");
            return true;
        }
        OutboxMessage claimed;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            if (await db.OutboxMessages.Where(m => m.Id == candidate.Id && m.RowVersion == candidate.RowVersion)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.Attempts, m => m.Attempts + 1).SetProperty(m => m.LeaseUntil, now.AddSeconds(options.Value.LeaseSeconds)), ct) == 0) return true;
            claimed = await db.OutboxMessages.AsNoTracking().SingleAsync(m => m.Id == candidate.Id, ct); await tx.CommitAsync(ct);
        }
        var code = "PAYLOAD_INVALID";
        try
        {
            var envelope = protector.Unprotect(claimed.ProtectedPayload); AuditSerialization.Validate(envelope.Record);
            var source = await db.AuditEvents.AsNoTracking().SingleOrDefaultAsync(a => a.Id == claimed.EventId, ct);
            if (source is null || envelope.EventId != claimed.EventId || envelope.OccurredAt != source.OccurredAt
                || envelope.Record.EventType != source.EventType || envelope.Record.Outcome != source.Outcome || envelope.Record.Reason != source.ReasonCode
                || envelope.Record.ActorUserId != source.ActorUserId || envelope.Record.SubjectId != source.SubjectId || envelope.Record.TraceId != source.TraceId
                || JsonSerializer.Serialize(envelope.Record.Metadata ?? new AuditMetadata(), AuditSerialization.Options) != source.SafeMetadata)
                throw new InvalidOperationException("Audit source mismatch.");
            code = "SINK_UNAVAILABLE"; sink.Write(envelope);
            await db.OutboxMessages.Where(m => m.Id == claimed.Id && m.RowVersion == claimed.RowVersion && m.LeaseUntil > clock.GetUtcNow())
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.ProcessedAt, clock.GetUtcNow()).SetProperty(m => m.LeaseUntil, (DateTimeOffset?)null).SetProperty(m => m.LastErrorCode, (string?)null), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            var failedAt = clock.GetUtcNow(); var dead = claimed.Attempts >= options.Value.MaxAttempts;
            var next = failedAt.AddSeconds(Math.Min(900, 30 * Math.Pow(2, Math.Min(claimed.Attempts - 1, 5))));
            var changed = await db.OutboxMessages.Where(m => m.Id == claimed.Id && m.RowVersion == claimed.RowVersion && m.LeaseUntil > failedAt)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.NextAttemptAt, next).SetProperty(m => m.LeaseUntil, (DateTimeOffset?)null)
                    .SetProperty(m => m.LastErrorCode, code).SetProperty(m => m.DeadLetteredAt, dead ? failedAt : (DateTimeOffset?)null), ct);
            if (changed == 1)
            {
                if (dead) Alert(claimed.Id, claimed.EventId, code);
                else logger.LogWarning("Audit delivery {MessageId}/{EventId} failed ({ErrorCode}); retry scheduled.", claimed.Id, claimed.EventId, code);
            }
        }
        return true;
    }
    private void Alert(Guid message, Guid eventId, string code) => logger.LogError("Audit dead letter {MessageId}/{EventId} ({ErrorCode}); operator action required.", message, eventId, code);
}
