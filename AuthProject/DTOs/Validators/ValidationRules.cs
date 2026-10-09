using System.ComponentModel.DataAnnotations;
using FluentValidation;

namespace AuthProject.DTOs.Validators;

internal static class ValidationRules
{
    public static IRuleBuilderOptions<T, string?> Email<T>(this IRuleBuilderInitial<T, string?> rule) => rule
        .Cascade(CascadeMode.Stop)
        .NotEmpty().WithErrorCode("EMAIL_REQUIRED").WithMessage("Email là bắt buộc.")
        .Must(value => value!.Trim().Length <= 254).WithErrorCode("EMAIL_LENGTH").WithMessage("Email tối đa 254 ký tự.")
        .Must(value => new EmailAddressAttribute().IsValid(value!.Trim())).WithErrorCode("EMAIL_INVALID").WithMessage("Email không hợp lệ.");

    public static IRuleBuilderOptions<T, string?> Password<T>(this IRuleBuilderInitial<T, string?> rule, bool isNew) => rule
        .Cascade(CascadeMode.Stop)
        .Must(value => value is { Length: > 0 }).WithErrorCode("PASSWORD_REQUIRED").WithMessage("Password là bắt buộc.")
        .Length(isNew ? 15 : 1, 128).WithErrorCode("PASSWORD_LENGTH").WithMessage(isNew
            ? "Password phải có từ 15 đến 128 ký tự." : "Password tối đa 128 ký tự.");

    public static IRuleBuilderOptions<T, string?> Token<T>(this IRuleBuilderInitial<T, string?> rule) => rule
        .Cascade(CascadeMode.Stop)
        .NotEmpty().WithErrorCode("TOKEN_REQUIRED").WithMessage("Token là bắt buộc.")
        .MaximumLength(512).WithErrorCode("TOKEN_LENGTH").WithMessage("Token tối đa 512 ký tự.")
        .Matches(@"\A[A-Za-z0-9_-]+\z").WithErrorCode("TOKEN_FORMAT").WithMessage("Token không đúng định dạng.");
}
