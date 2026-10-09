using AuthProject.Entities;

namespace AuthProject.Logging;

// Only allow typed metadata. Never accept request bodies, arbitrary dictionaries or credentials.
public sealed record AuditMetadata(Guid? SessionId = null, AuthenticationMethod? Method = null, int? Count = null);
public sealed record AuditRecord(AuditEventType EventType, AuditOutcome Outcome, AuditReason Reason,
    string TraceId, Guid? ActorUserId = null, Guid? SubjectId = null, AuditMetadata? Metadata = null);
public sealed record AuditEnvelope(Guid EventId, DateTimeOffset OccurredAt, AuditRecord Record);

public interface IAuditWriter
{
    Guid Stage(AuditRecord record);
}

public interface IAuditPayloadProtector
{
    string Protect(AuditEnvelope envelope);
    AuditEnvelope Unprotect(string protectedPayload);
}

public interface IAuditSink
{
    void Write(AuditEnvelope envelope);
}
