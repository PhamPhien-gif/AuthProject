using AuthProject.IntegrationTests.Hosting;
using AuthProject.TestSupport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace AuthProject.IntegrationTests.Foundation;

[Trait("Category", "Integration"), Trait("Dependency", "None")]
public sealed class RefreshStartupTests
{
    [Theory]
    [InlineData("missing-origins")]
    [InlineData("http")]
    [InlineData("path")]
    [InlineData("wildcard")]
    [InlineData("duplicate")]
    [InlineData("jwt-disabled")]
    public async Task EnabledRefresh_RejectsUnsafeConfigurationAtStartup(string state)
    {
        using var key = new TemporaryJwtKey();
        await using var host = new AuthWebApplicationFactory().WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
        {
            var values = new Dictionary<string, string?>
            {
                ["Persistence:Enabled"] = "true", ["ConnectionStrings:DefaultConnection"] = "Server=local-test-only;Database=AuthProject_Test_Config;Integrated Security=True;",
                ["Auth:Jwt:Enabled"] = state == "jwt-disabled" ? "false" : "true", ["Auth:Jwt:Issuer"] = key.Settings.Issuer,
                ["Auth:Jwt:Audience"] = key.Settings.Audience, ["Auth:Jwt:KeyId"] = key.Settings.KeyId, ["Auth:Jwt:SigningKeyPath"] = key.Path,
                ["Auth:Refresh:Enabled"] = "true"
            };
            if (state != "missing-origins") values["Auth:Refresh:AllowedOrigins:0"] = state switch
            {
                "http" => "http://localhost", "path" => "https://localhost/path", "wildcard" => "https://*.example.test", _ => "https://localhost"
            };
            if (state == "duplicate") values["Auth:Refresh:AllowedOrigins:1"] = "https://localhost";
            config.AddInMemoryCollection(values);
        }));
        var exception = Assert.Throws<OptionsValidationException>(() => host.CreateClient());
        Assert.DoesNotContain(key.Path, exception.Message);
        Assert.DoesNotContain("PRIVATE KEY", exception.Message);
    }
}
