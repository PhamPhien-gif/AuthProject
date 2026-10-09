using AuthProject.Data;
using AuthProject.Entities;
using System.Text.Json;
using System.Text.Json.Serialization;
using log4net;
using log4net.Config;
using log4net.Repository;
using Microsoft.AspNetCore.DataProtection;

namespace AuthProject.Logging;

public sealed class AuditPayloadProtector(IDataProtectionProvider provider) : IAuditPayloadProtector
{
    private readonly IDataProtector protector = provider.CreateProtector("AuthProject.AuditOutbox.v1");
    public string Protect(AuditEnvelope envelope) => protector.Protect(AuditSerialization.Serialize(envelope));
    public AuditEnvelope Unprotect(string protectedPayload) =>
        JsonSerializer.Deserialize<AuditEnvelope>(protector.Unprotect(protectedPayload), AuditSerialization.Options)
        ?? throw new InvalidOperationException("Invalid audit envelope.");
}

public sealed class AuditWriter(AuthDbContext context, IAuditPayloadProtector protector, TimeProvider clock) : IAuditWriter
{
    // Stage only: the use case commits state + audit + outbox through one unit of work.
    public Guid Stage(AuditRecord record)
    {
        AuditSerialization.Validate(record);
        var envelope = new AuditEnvelope(Guid.NewGuid(), clock.GetUtcNow(), record);
        var payload = protector.Protect(envelope); // Protect before staging either row.
        context.AuditEvents.Add(new AuditEvent
        {
            Id = envelope.EventId, ActorUserId = record.ActorUserId, SubjectId = record.SubjectId,
            EventType = record.EventType, Outcome = record.Outcome, ReasonCode = record.Reason,
            OccurredAt = envelope.OccurredAt, TraceId = record.TraceId,
            SafeMetadata = JsonSerializer.Serialize(record.Metadata ?? new AuditMetadata(), AuditSerialization.Options)
        });
        context.OutboxMessages.Add(new OutboxMessage
        {
            EventId = envelope.EventId, Kind = OutboxKind.Audit, ProtectedPayload = payload,
            CreatedAt = envelope.OccurredAt, NextAttemptAt = envelope.OccurredAt
        });
        return envelope.EventId;
    }
}

public sealed class Log4NetAuditSink(ILog log) : IAuditSink, IDisposable
{
    private ILoggerRepository? ownedRepository;

    internal static Log4NetAuditSink Create(bool enabled, string configurationFile)
    {
        if (!enabled) return new Log4NetAuditSink(LogManager.GetLogger("Auth.Audit.Disabled")) { disabled = true };
        var repository = LogManager.CreateRepository("Auth.Audit." + Guid.NewGuid());
        try
        {
            if (!File.Exists(configurationFile)) throw new InvalidOperationException("Audit log4net configuration is missing.");
            XmlConfigurator.Configure(repository, new FileInfo(configurationFile));
            return new Log4NetAuditSink(LogManager.GetLogger(repository.Name, "Auth.Audit")) { ownedRepository = repository };
        }
        catch { repository.Shutdown(); throw; }
    }

    private bool disabled;
    public void Write(AuditEnvelope envelope)
    {
        if (disabled) throw new InvalidOperationException("The audit file sink is disabled.");
        AuditSerialization.Validate(envelope.Record);
        log.Info(AuditSerialization.Serialize(envelope));
    }

    public void Dispose() => ownedRepository?.Shutdown();
}

internal static class AuditSerialization
{
    internal static readonly JsonSerializerOptions Options = CreateOptions();
    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
        return options;
    }

    internal static string Serialize(AuditEnvelope envelope) => JsonSerializer.Serialize(envelope, Options);

    internal static void Validate(AuditRecord record)
    {
        // TraceId is the server's trace identifier, never an arbitrary client field.
        if (string.IsNullOrWhiteSpace(record.TraceId) || record.TraceId.Length > 64
            || record.TraceId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ':' and not '-' and not '_' and not '.'))
            throw new ArgumentException("Audit trace identifier is invalid.", nameof(record));
        if (!Enum.IsDefined(record.EventType) || !Enum.IsDefined(record.Outcome) || !Enum.IsDefined(record.Reason)
            || record.Metadata?.Count is < 0
            || record.Metadata?.Method is { } method && !Enum.IsDefined(method))
            throw new ArgumentException("Audit metadata is invalid.", nameof(record));
    }
}
