using AuthProject.Security;

namespace UnitTestAuth.Security;

[Trait("Category", "Unit")]
public sealed class RefreshTokenSecretTests
{
    [Fact]
    public void RandomSecrets_AreUniqueAndOnlyTheirDigestsArePersisted()
    {
        var tokens = Enumerable.Range(0, 100).Select(_ => RefreshTokenSecret.Create()).ToArray();
        Assert.Equal(100, tokens.Distinct().Count());
        Assert.All(tokens, token => { Assert.Equal(43, token.Length); Assert.Equal(64, RefreshTokenSecret.Digest(token)!.Length); Assert.NotEqual(token, RefreshTokenSecret.Digest(token)); });
    }
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-token")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAB")]
    public void InvalidOrNonCanonicalSecret_IsRejected(string? token) => Assert.Null(RefreshTokenSecret.Digest(token));
}
