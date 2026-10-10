using System.Security.Claims;
using AuthProject.Security;
using AuthProject.Exceptions;

namespace UnitTestAuth.Security;

[Trait("Category", "Unit")]
public sealed class GoogleIdentityTests
{
    private static ClaimsPrincipal Principal(params Claim[] extra) => new(new ClaimsIdentity(new[]
    { new Claim("sub", "stable-subject"), new Claim("email", "user@example.test"), new Claim("email_verified", "true"), new Claim("name", "User") }.Concat(extra)));
    [Fact]
    public void Validated_principal_uses_subject_and_verified_email()
    {
        var identity = GoogleIdentity.Read(Principal());
        Assert.Equal("stable-subject", identity.Subject); Assert.Equal("user@example.test", identity.Email);
    }
    [Theory]
    [InlineData("sub", "other")] [InlineData("email", "attacker@example.test")] [InlineData("email_verified", "false")]
    public void Duplicate_security_claims_are_rejected(string claim, string value) =>
        Assert.Throws<AppException>(() => GoogleIdentity.Read(Principal(new Claim(claim, value))));
    [Theory]
    [InlineData("false")] [InlineData("1")] [InlineData("")]
    public void Unverified_provider_email_is_rejected(string verified)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("sub", "subject"), new Claim("email", "user@example.test"), new Claim("email_verified", verified) }));
        Assert.Throws<AppException>(() => GoogleIdentity.Read(principal));
    }
}
