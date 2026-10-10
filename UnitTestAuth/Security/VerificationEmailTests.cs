using AuthProject.Services.Email;
using Microsoft.AspNetCore.DataProtection;

namespace UnitTestAuth.Security;

[Trait("Category", "Unit")]
public sealed class VerificationEmailTests
{
    [Fact]
    public void ProtectedPayload_HidesCredentialsAndRoundTrips()
    {
        var protector = new EmailPayloadProtector(new EphemeralDataProtectionProvider());
        var email = new VerificationEmail(Guid.NewGuid(), Guid.NewGuid(), "person@example.test", "secret-token", DateTimeOffset.UtcNow.AddHours(1));
        var payload = protector.Protect(email);
        Assert.DoesNotContain(email.Recipient, payload);
        Assert.DoesNotContain(email.Token, payload);
        Assert.Equal(email, protector.Unprotect(payload));
    }

    [Fact]
    public void ProtectedPayload_RejectsTamperingAndDifferentPurpose()
    {
        var provider = new EphemeralDataProtectionProvider();
        var protector = new EmailPayloadProtector(provider);
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => protector.Unprotect("bad-payload"));
        var different = provider.CreateProtector("AuthProject.AuditOutbox.v1").Protect("{}");
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => protector.Unprotect(different));
    }

    [Fact]
    public void Template_UsesConfiguredUrlFragmentAndExpiryWithoutHttpQuery()
    {
        var email = new VerificationEmail(Guid.NewGuid(), Guid.NewGuid(), "person@example.test", "abc_def-123", DateTimeOffset.UtcNow.AddHours(1));
        var content = VerificationEmailTemplate.Render(email, "https://client.example.test/verify-email");
        Assert.Contains("https://client.example.test/verify-email#token=abc_def-123", content.TextBody);
        Assert.DoesNotContain("?token=", content.TextBody);
        Assert.Contains(email.ExpiresAt.ToString("O"), content.TextBody);
    }
}
