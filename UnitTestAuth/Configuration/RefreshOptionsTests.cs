using AuthProject.Configuration;

namespace UnitTestAuth.Configuration;

[Trait("Category", "Unit")]
public sealed class RefreshOptionsTests
{
    [Theory]
    [InlineData("https://localhost", true)]
    [InlineData("https://localhost:7148", true)]
    [InlineData("http://localhost", false)]
    [InlineData("https://localhost/", false)]
    [InlineData("https://localhost/path", false)]
    [InlineData("https://*.example.test", false)]
    [InlineData("https://user:password@example.test", false)]
    [InlineData("https://example.test?query=1", false)]
    [InlineData("https://example.test#fragment", false)]
    [InlineData("null", false)]
    public void Origins_MustBeExactHttpsAuthorities(string origin, bool valid) => Assert.Equal(valid, RefreshOptions.IsOrigin(origin));
}
