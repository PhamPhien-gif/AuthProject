using AuthProject.Security;

namespace UnitTestAuth.Security;

[Trait("Category", "Unit")]
public sealed class EmailVerificationTokenTests
{
    [Fact]
    public void RandomToken_HasBoundedCanonicalEncodingAndDistinctDigest()
    {
        var first = EmailVerificationToken.Create();
        var second = EmailVerificationToken.Create();
        Assert.Equal(43, first.Length);
        Assert.NotEqual(first, second);
        Assert.Equal(64, EmailVerificationToken.Digest(first)!.Length);
        Assert.NotEqual(EmailVerificationToken.Digest(first), EmailVerificationToken.Digest(second));
        Assert.Equal(EmailVerificationToken.Digest(first), EmailVerificationToken.Digest(first));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("invalid")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAB")]
    public void InvalidOrNonCanonicalToken_HasNoDigest(string? token) => Assert.Null(EmailVerificationToken.Digest(token));
}
