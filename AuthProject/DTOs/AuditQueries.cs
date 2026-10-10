using AuthProject.Logging;

namespace AuthProject.DTOs;

public sealed class AuditQuery : HistoryQuery
{
    public Guid? ActorUserId { get; init; }
    public Guid? SubjectId { get; init; }
    public string? EventType { get; init; }
    public string? Outcome { get; init; }
    public string? Reason { get; init; }
    public string? TraceId { get; init; }
}
public sealed record LoginHistoryResponse(Guid Id, DateTimeOffset OccurredAt, string Method, string Outcome,
    string ReasonCode, Guid? SessionId, string? IpPrefix);
public sealed record AuditResponse(Guid EventId, DateTimeOffset OccurredAt, Guid? ActorUserId, Guid? SubjectId,
    string EventType, string Outcome, string ReasonCode, string TraceId, AuditMetadata Metadata);
