using AuthProject.Configuration;
using AuthProject.Entities;
using AuthProject.Security;
using Microsoft.Extensions.Options;

namespace UnitTestAuth.Security;

[Trait("Category", "Unit")]
public sealed class EmailOtpSecretTests
{
    private static EmailOtpSecret Secret() => new(Options.Create(new EmailOtpOptions
    { Pepper = Convert.ToBase64String(new byte[32]), KeyId = "test" }));
    [Fact]
    public void Generated_codes_are_six_ASCII_digits()
    {
        for (var i = 0; i < 100; i++) Assert.Matches("^[0-9]{6}$", EmailOtpSecret.Create());
    }
    [Fact]
    public void Digest_binds_all_challenge_fields_and_version()
    {
        var secret = Secret();
        var challenge = new AuthChallenge { UserId = Guid.NewGuid(), ContextId = Guid.NewGuid(),
            Purpose = ChallengePurpose.LoginMfa, AuthVersion = 1, KeyId = "test" };
        var digest = secret.Digest(challenge, "012345");
        Assert.DoesNotContain("012345", digest);
        Assert.True(secret.Matches(challenge, "012345", digest));
        Assert.False(secret.Matches(challenge, "012346", digest));
        challenge.Version++;
        Assert.False(secret.Matches(challenge, "012345", digest));
        challenge.Version--;
        challenge.Purpose = ChallengePurpose.EnrollMfa;
        Assert.False(secret.Matches(challenge, "012345", digest));
        challenge.Purpose = ChallengePurpose.LoginMfa;
        challenge.ContextId = Guid.NewGuid();
        Assert.False(secret.Matches(challenge, "012345", digest));
    }
    [Theory]
    [InlineData(null)] [InlineData("12345")] [InlineData("１２３４５６")] [InlineData("123456 ")]
    public void Invalid_codes_never_match(string? code)
    {
        var secret = Secret();
        var challenge = new AuthChallenge { KeyId = "test" };
        Assert.False(secret.Matches(challenge, code, secret.Digest(challenge, "123456")));
    }
}
