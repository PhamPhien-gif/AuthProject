using FluentValidation;

namespace AuthProject.DTOs.Validators;

public sealed class RegisterRequestValidator : AbstractValidator<RegisterRequest>
{
    public RegisterRequestValidator()
    {
        RuleFor(x => x.Email).Email();
        RuleFor(x => x.Password).Password(isNew: true);
        RuleFor(x => x.DisplayName).Cascade(CascadeMode.Stop)
            .NotEmpty().WithErrorCode("DISPLAY_NAME_REQUIRED").WithMessage("Tên hiển thị là bắt buộc.")
            .Must(value => value!.Trim().Length <= 100).WithErrorCode("DISPLAY_NAME_LENGTH").WithMessage("Tên hiển thị tối đa 100 ký tự.");
    }
}

public sealed class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    public LoginRequestValidator() { RuleFor(x => x.Email).Email(); RuleFor(x => x.Password).Password(isNew: false); }
}
public sealed class EmailRequestValidator : AbstractValidator<EmailRequest>
{
    public EmailRequestValidator() => RuleFor(x => x.Email).Email();
}
public sealed class VerifyEmailRequestValidator : AbstractValidator<VerifyEmailRequest>
{
    public VerifyEmailRequestValidator() => RuleFor(x => x.Token).Token();
}
public sealed class ResetPasswordRequestValidator : AbstractValidator<ResetPasswordRequest>
{
    public ResetPasswordRequestValidator() { RuleFor(x => x.Token).Token(); RuleFor(x => x.NewPassword).Password(isNew: true); }
}
public sealed class ChangePasswordRequestValidator : AbstractValidator<ChangePasswordRequest>
{
    public ChangePasswordRequestValidator()
    {
        RuleFor(x => x.CurrentPassword).Password(isNew: false);
        RuleFor(x => x.NewPassword).Password(isNew: true)
            .NotEqual(x => x.CurrentPassword).WithErrorCode("PASSWORD_UNCHANGED").WithMessage("Password mới phải khác password hiện tại.");
    }
}
public sealed class ReauthenticateRequestValidator : AbstractValidator<ReauthenticateRequest>
{
    public ReauthenticateRequestValidator() => RuleFor(x => x.Password).Password(isNew: false);
}
public sealed class VerifyOtpRequestValidator : AbstractValidator<VerifyOtpRequest>
{
    public VerifyOtpRequestValidator()
    {
        RuleFor(x => x.ChallengeId).NotEmpty().WithErrorCode("CHALLENGE_REQUIRED").WithMessage("Challenge không hợp lệ.");
        RuleFor(x => x.Code).Cascade(CascadeMode.Stop).NotEmpty().WithErrorCode("OTP_REQUIRED").WithMessage("OTP là bắt buộc.")
            .Matches(@"\A[0-9]{6}\z").WithErrorCode("OTP_FORMAT").WithMessage("OTP phải có đúng 6 chữ số ASCII.");
    }
}
public sealed class ChallengeRequestValidator : AbstractValidator<ChallengeRequest>
{
    public ChallengeRequestValidator() => RuleFor(x => x.ChallengeId).NotEmpty().WithErrorCode("CHALLENGE_REQUIRED").WithMessage("Challenge không hợp lệ.");
}
public sealed class RecoveryCodeRequestValidator : AbstractValidator<RecoveryCodeRequest>
{
    public RecoveryCodeRequestValidator()
    {
        RuleFor(x => x.ChallengeId).NotEmpty().WithErrorCode("CHALLENGE_REQUIRED").WithMessage("Challenge không hợp lệ.");
        RuleFor(x => x.Code).Token().Length(43).WithErrorCode("RECOVERY_CODE_FORMAT").WithMessage("Recovery code phải có 43 ký tự Base64URL.");
    }
}
public sealed class GoogleCompleteRequestValidator : AbstractValidator<GoogleCompleteRequest>
{
    public GoogleCompleteRequestValidator() => RuleFor(x => x.Ticket).Token();
}
public sealed class PagingRequestValidator : AbstractValidator<PagingRequest>
{
    public PagingRequestValidator()
    {
        RuleFor(x => x.Page).GreaterThanOrEqualTo(1).WithErrorCode("PAGE_INVALID").WithMessage("Page phải lớn hơn hoặc bằng 1.");
        RuleFor(x => x.PageSize).InclusiveBetween(1, 100).WithErrorCode("PAGE_SIZE_INVALID").WithMessage("PageSize phải từ 1 đến 100.");
    }
}
public sealed class HistoryQueryValidator : AbstractValidator<HistoryQuery>
{
    public HistoryQueryValidator()
    {
        Include(new PagingRequestValidator());
        RuleFor(x => x.From).Must(value => value is null || value.Value.Offset == TimeSpan.Zero)
            .WithErrorCode("DATE_UTC_REQUIRED").WithMessage("Thời gian phải dùng UTC.");
        RuleFor(x => x.To).Must(value => value is null || value.Value.Offset == TimeSpan.Zero)
            .WithErrorCode("DATE_UTC_REQUIRED").WithMessage("Thời gian phải dùng UTC.");
        RuleFor(x => x).Must(x => (x.From is null && x.To is null) ||
            (x.From is not null && x.To is not null && x.From <= x.To && x.To - x.From <= TimeSpan.FromDays(90)))
            .OverridePropertyName("from").WithErrorCode("DATE_RANGE_INVALID")
            .WithMessage("Cung cấp cả from/to theo đúng thứ tự, trong khoảng tối đa 90 ngày.");
        RuleFor(x => x.Sort).Must(value => value is "occurredAtAsc" or "occurredAtDesc")
            .WithErrorCode("SORT_INVALID").WithMessage("Sort không được hỗ trợ.");
    }
}
public sealed class SetUserRolesRequestValidator : AbstractValidator<SetUserRolesRequest>
{
    public SetUserRolesRequestValidator()
    {
        RuleFor(x => x.RoleNames).Cascade(CascadeMode.Stop)
            .NotNull().WithErrorCode("ROLES_REQUIRED").WithMessage("Danh sách role là bắt buộc.")
            .Must(value => value!.Length is > 0 and <= 10 && value.Distinct(StringComparer.OrdinalIgnoreCase).Count() == value.Length)
            .WithErrorCode("ROLES_INVALID").WithMessage("Cần từ 1 đến 10 role không trùng.")
            .Must(value => value!.All(role => role is "User" or "Admin"))
            .WithErrorCode("ROLE_UNKNOWN").WithMessage("Role không thuộc catalog User/Admin.");
    }
}
public sealed class ChangeUserStatusRequestValidator : AbstractValidator<ChangeUserStatusRequest>
{
    public ChangeUserStatusRequestValidator() => RuleFor(x => x.Status).Must(value => value is "Active" or "Disabled")
        .WithErrorCode("STATUS_INVALID").WithMessage("Status phải là Active hoặc Disabled.");
}
