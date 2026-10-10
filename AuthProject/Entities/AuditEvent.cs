namespace AuthProject.Entities;

public sealed class AuditEvent : EntityBase
{
    public Guid? ActorUserId { get; set; }
    public Guid? SubjectId { get; set; }
    public AuditEventType EventType { get; set; }
    public AuditOutcome Outcome { get; set; }
    public AuditReason ReasonCode { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public string TraceId { get; set; } = string.Empty;
    public string SafeMetadata { get; set; } = "{}";
}

public sealed class LoginHistory : EntityBase
{
    public Guid? UserId { get; set; }
    public AuthenticationMethod Method { get; set; }
    public AuditOutcome Outcome { get; set; }
    public AuditReason ReasonCode { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public Guid? SessionId { get; set; }
    public string TraceId { get; set; } = string.Empty;
}

public sealed class OutboxMessage : VersionedEntity
{
    public Guid EventId { get; set; }
    public OutboxKind Kind { get; set; }
    public string ProtectedPayload { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public DateTimeOffset? DeadLetteredAt { get; set; }
    public string? LastErrorCode { get; set; }
}
