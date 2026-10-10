using AuthProject.Data;
using AuthProject.Entities;
using System.Text.Json;
using System.Text.Json.Serialization;
using log4net;
using log4net.Config;
using log4net.Repository;
using Microsoft.AspNetCore.DataProtection;
using AuthProject.Configuration;
using log4net.Appender;
using log4net.Core;
using System.Xml.Linq;

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

public sealed class AuditFileErrorHandler : IErrorHandler
{
    private int errors;
    public int Errors => Volatile.Read(ref errors);
    public void Error(string message) => Interlocked.Increment(ref errors);
    public void Error(string message, Exception exception) => Interlocked.Increment(ref errors);
    public void Error(string message, Exception? exception, ErrorCode errorCode) => Interlocked.Increment(ref errors);
}

public sealed class Log4NetAuditSink : IAuditSink, IDisposable
{
    private readonly ILog log;
    private readonly object gate = new();
    private readonly AuditFileErrorHandler[] handlers;
    public Log4NetAuditSink(ILog log)
    {
        this.log = log;
        handlers = (log.Logger.Repository?.GetAppenders() ?? []).OfType<AppenderSkeleton>().Select(a =>
        {
            var handler = a.ErrorHandler as AuditFileErrorHandler ?? new AuditFileErrorHandler();
            a.ErrorHandler = handler; return handler;
        }).ToArray();
    }
    private ILoggerRepository? ownedRepository;

    internal static Log4NetAuditSink Create(AuditLogOptions options, string contentRoot, string configurationFile)
    {
        if (!options.Enabled) return new Log4NetAuditSink(LogManager.GetLogger("Auth.Audit.Disabled")) { disabled = true };
        var repository = LogManager.CreateRepository("Auth.Audit." + Guid.NewGuid());
        try
        {
            if (!File.Exists(configurationFile)) throw new InvalidOperationException("Audit log4net configuration is missing.");
            var directory = Path.GetFullPath(options.Directory, contentRoot);
            System.IO.Directory.CreateDirectory(directory);
            var config = XElement.Load(configurationFile); var appender = config.Element("appender")!;
            appender.Element("file")!.SetAttributeValue("value", Path.Combine(directory, "audit-" + options.InstanceId + ".jsonl"));
            appender.Element("maximumFileSize")!.SetAttributeValue("value", options.MaximumFileSizeMb + "MB");
            appender.Element("maxSizeRollBackups")!.SetAttributeValue("value", options.MaxBackups);
            var document = new System.Xml.XmlDocument(); document.LoadXml(config.ToString());
            XmlConfigurator.Configure(repository, document.DocumentElement!);
            return new Log4NetAuditSink(LogManager.GetLogger(repository.Name, "Auth.Audit")) { ownedRepository = repository };
        }
        catch { repository.Shutdown(); throw; }
    }

    private bool disabled;
    public void Write(AuditEnvelope envelope)
    {
        if (disabled) throw new InvalidOperationException("The audit file sink is disabled.");
        AuditSerialization.Validate(envelope.Record);
        lock (gate)
        {
            if (!log.IsInfoEnabled || handlers.Length == 0) throw new IOException("Audit sink is unavailable.");
            var before = handlers.Sum(h => h.Errors);
            log.Info(AuditSerialization.Serialize(envelope));
            if (handlers.Sum(h => h.Errors) != before) throw new IOException("Audit sink write failed.");
        }
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
