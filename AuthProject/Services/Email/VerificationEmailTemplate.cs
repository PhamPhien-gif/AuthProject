namespace AuthProject.Services.Email;

public static class VerificationEmailTemplate
{
    public static EmailContent Render(VerificationEmail email, string verificationUrl) => new("Xác minh email AuthProject",
        "Vui lòng mở liên kết sau để xác minh địa chỉ email:\n" + verificationUrl + "#token=" + Uri.EscapeDataString(email.Token)
        + "\nLiên kết có hiệu lực đến " + email.ExpiresAt.ToString("O") + ".\nNếu bạn không gửi yêu cầu này, hãy bỏ qua email.");
}
