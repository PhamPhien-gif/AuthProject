using AuthProject.IntegrationTests.Hosting;
using AuthProject.TestSupport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace AuthProject.IntegrationTests.Foundation;

[Trait("Category", "Integration"), Trait("Dependency", "None")]
public sealed class GoogleStartupTests
{
    [Theory]
    [InlineData("refresh")] [InlineData("http")] [InlineData("query")] [InlineData("fragment")]
    [InlineData("origin")] [InlineData("callback")] [InlineData("secret")]
    public async Task Unsafe_Google_configuration_fails_at_startup_without_secrets(string state)
    {
        using var key = new TemporaryJwtKey(); await using var root = new AuthWebApplicationFactory();
        await using var host = root.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string,string?>
        {
            ["Persistence:Enabled"] = "true", ["ConnectionStrings:DefaultConnection"] = "Server=local-test-only;Database=AuthProject_Test_Config;Integrated Security=True;",
            ["Auth:Jwt:Enabled"] = "true", ["Auth:Jwt:Issuer"] = key.Settings.Issuer, ["Auth:Jwt:Audience"] = key.Settings.Audience,
            ["Auth:Jwt:KeyId"] = key.Settings.KeyId, ["Auth:Jwt:SigningKeyPath"] = key.Path,
            ["Auth:Refresh:Enabled"] = state == "refresh" ? "false" : "true", ["Auth:Refresh:AllowedOrigins:0"] = "https://localhost",
            ["Auth:Google:Enabled"] = "true", ["Auth:Google:ClientId"] = "client", ["Auth:Google:ClientSecret"] = state == "secret" ? new string('x', 513) : "DO_NOT_LEAK_GOOGLE_SECRET",
            ["Auth:Google:CallbackUri"] = state == "callback" ? "https://localhost/api/v1/auth/me" : "https://localhost/signin-google",
            ["Auth:Google:CompletionUrl"] = state == "http" ? "http://localhost/complete" : state == "query" ? "https://localhost/complete?token=bad"
                : state == "fragment" ? "https://localhost/complete#bad" : state == "origin" ? "https://attacker.test/complete" : "https://localhost/complete",
            ["Auth:Google:ErrorUrl"] = "https://localhost/error"
        })));
        var ex = Assert.ThrowsAny<Exception>(() => host.CreateClient());
        if (ex is AggregateException multiple) Assert.All(multiple.Flatten().InnerExceptions, e => Assert.IsType<OptionsValidationException>(e));
        else Assert.IsType<OptionsValidationException>(ex);
        Assert.DoesNotContain("DO_NOT_LEAK_GOOGLE_SECRET", ex.Message);
    }
}
