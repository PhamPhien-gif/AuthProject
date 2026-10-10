using FluentValidation;

namespace AuthProject.DTOs.Validators;

public sealed class AdminUserQueryValidator : AbstractValidator<AdminUserQuery>
{
    public AdminUserQueryValidator()
    {
        Include(new PagingRequestValidator());
        RuleFor(x => x.Status).Must(s => s is null or "Active" or "Disabled" or "PendingVerification")
            .WithErrorCode("STATUS_INVALID").WithMessage("Status không hợp lệ.");
        RuleFor(x => x.Search).MaximumLength(100).WithErrorCode("SEARCH_TOO_LONG").WithMessage("Search tối đa 100 ký tự.");
    }
}
public sealed class UserTargetRequestValidator : AbstractValidator<UserTargetRequest>
{
    public UserTargetRequestValidator() => RuleFor(x => x.UserId).NotEmpty().WithErrorCode("USER_REQUIRED").WithMessage("UserId không hợp lệ.");
}
public sealed class UpdateProfileRequestValidator : AbstractValidator<UpdateProfileRequest>
{
    public UpdateProfileRequestValidator() => RuleFor(x => x.DisplayName).Cascade(CascadeMode.Stop).NotEmpty()
        .WithErrorCode("DISPLAY_NAME_REQUIRED").WithMessage("Tên hiển thị là bắt buộc.")
        .Must(v => v!.Trim().Length is > 0 and <= 100).WithErrorCode("DISPLAY_NAME_LENGTH").WithMessage("Tên hiển thị tối đa 100 ký tự.");
}
