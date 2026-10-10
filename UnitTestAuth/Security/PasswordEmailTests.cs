using AuthProject.Services.Email;
using AuthProject.Security;
using Microsoft.AspNetCore.DataProtection;

namespace UnitTestAuth.Security;

[Trait("Category", "Unit")]
public sealed class PasswordEmailTests
{
    [Fact]
    public void Payload_IsProtectedAndSeparatedFromVerificationPurpose()
    {
        var provider = new EphemeralDataProtectionProvider();
        var protector = new PasswordEmailProtector(provider);
        var token = EmailVerificationToken.Create();
        var email = new PasswordEmail(PasswordEmailPurpose.ResetLink, Guid.NewGuid(), "user@example.test", 1,
            DateTimeOffset.UtcNow, Guid.NewGuid(), token, DateTimeOffset.UtcNow.AddMinutes(30));
        var ciphertext = protector.Protect(email);
        Assert.DoesNotContain(token, ciphertext); Assert.DoesNotContain(email.Recipient, ciphertext);
        Assert.Equal(email, protector.Unprotect(ciphertext));
        Assert.Throws<System.Security.Cryptography.CryptographicException>(() => new EmailPayloadProtector(provider).Unprotect(ciphertext));
    }
    [Fact]
    public void ResetTemplate_UsesFragmentAndNotificationDoesNotContainResetSecret()
    {
        var email = new PasswordEmail(PasswordEmailPurpose.ResetLink, Guid.NewGuid(), "user@example.test", 1,
            DateTimeOffset.UtcNow, Guid.NewGuid(), EmailVerificationToken.Create(), DateTimeOffset.UtcNow.AddMinutes(30));
        var content = PasswordEmailTemplate.Render(email, "https://client.example.test/reset");
        Assert.Contains("/reset#token=", content.TextBody); Assert.DoesNotContain("?token", content.TextBody);
        var notification = PasswordEmailTemplate.Render(email with { Purpose = PasswordEmailPurpose.PasswordChanged, Token = null }, "https://client.example.test/reset");
        Assert.DoesNotContain(email.Token!, notification.TextBody); Assert.DoesNotContain("#token", notification.TextBody);
    }
}
