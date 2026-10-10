using AuthProject.Entities;
using FluentValidation;

namespace AuthProject.DTOs.Validators;

public sealed class AuditQueryValidator : AbstractValidator<AuditQuery>
{
    public AuditQueryValidator()
    {
        Include(new HistoryQueryValidator());
        RuleFor(x => x.ActorUserId).Must(v => v != Guid.Empty).WithErrorCode("USER_INVALID").WithMessage("ActorUserId không hợp lệ.");
        RuleFor(x => x.SubjectId).Must(v => v != Guid.Empty).WithErrorCode("USER_INVALID").WithMessage("SubjectId không hợp lệ.");
        RuleFor(x => x.EventType).Must(v => v is null || Enum.GetNames<AuditEventType>().Contains(v)).WithErrorCode("EVENT_INVALID").WithMessage("EventType không hợp lệ.");
        RuleFor(x => x.Outcome).Must(v => v is null || Enum.GetNames<AuditOutcome>().Contains(v)).WithErrorCode("OUTCOME_INVALID").WithMessage("Outcome không hợp lệ.");
        RuleFor(x => x.Reason).Must(v => v is null || Enum.GetNames<AuditReason>().Contains(v)).WithErrorCode("REASON_INVALID").WithMessage("Reason không hợp lệ.");
        RuleFor(x => x.TraceId).Must(v => v is null || v.Length is > 0 and <= 64 && v.All(c => char.IsAsciiLetterOrDigit(c) || c is ':' or '-' or '_' or '.'))
            .WithErrorCode("TRACE_INVALID").WithMessage("TraceId không hợp lệ.");
    }
}
