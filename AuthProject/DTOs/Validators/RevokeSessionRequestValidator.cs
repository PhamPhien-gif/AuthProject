using FluentValidation;

namespace AuthProject.DTOs.Validators;

public sealed class RevokeSessionRequestValidator : AbstractValidator<RevokeSessionRequest>
{
    public RevokeSessionRequestValidator() => RuleFor(x => x.SessionId).NotEmpty()
        .WithErrorCode("SESSION_ID_REQUIRED").WithMessage("Session không hợp lệ.");
}
