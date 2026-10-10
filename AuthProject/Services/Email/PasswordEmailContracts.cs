using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace AuthProject.Services.Email;

public enum PasswordEmailPurpose { ResetLink, PasswordChanged }
public sealed record PasswordEmail(PasswordEmailPurpose Purpose, Guid UserId, string Recipient,
    long AuthVersion, DateTimeOffset OccurredAt, Guid? ChallengeId = null, string? Token = null, DateTimeOffset? ExpiresAt = null);
public interface IPasswordEmailProtector
{
    string Protect(PasswordEmail email);
    PasswordEmail Unprotect(string value);
}
public sealed class PasswordEmailProtector(IDataProtectionProvider provider) : IPasswordEmailProtector
{
    private readonly IDataProtector protector = provider.CreateProtector("AuthProject.PasswordEmail.v1");
    public string Protect(PasswordEmail email) => protector.Protect(JsonSerializer.Serialize(email));
    public PasswordEmail Unprotect(string value) => JsonSerializer.Deserialize<PasswordEmail>(protector.Unprotect(value))
        ?? throw new InvalidOperationException("Invalid password email payload.");
}
public static class PasswordEmailTemplate
{
    public static EmailContent Render(PasswordEmail email, string resetUrl) => email.Purpose switch
    {
        PasswordEmailPurpose.ResetLink => new("Đặt lại mật khẩu AuthProject", "Mở liên kết để đặt lại mật khẩu:\n"
            + resetUrl + "#token=" + Uri.EscapeDataString(email.Token!) + "\nCó hiệu lực đến " + email.ExpiresAt!.Value.ToString("O")
            + ".\nNếu không yêu cầu, hãy bỏ qua email. Reset không tắt MFA."),
        PasswordEmailPurpose.PasswordChanged => new("Mật khẩu AuthProject đã thay đổi", "Mật khẩu của bạn đã thay đổi lúc "
            + email.OccurredAt.ToString("O") + ". Các phiên cũ đã được thu hồi.\nNếu bạn không thực hiện thao tác này, hãy khôi phục tài khoản và kiểm tra bảo mật email."),
        _ => throw new InvalidOperationException("Unknown password email purpose.")
    };
}
