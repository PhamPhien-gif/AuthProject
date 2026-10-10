using System.Net.Http.Json;
using System.Security.Cryptography;
using AuthProject.IntegrationTests.Hosting;
using AuthProject.TestSupport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace AuthProject.IntegrationTests.Foundation;

[Trait("Category", "Integration")]
[Trait("Dependency", "None")]
public sealed class LoginStartupTests
{
    [Fact]
    public async Task DisabledLogin_IsUnavailableAndGetMeRequiresBearerWithoutSql()
    {
        await using var host = new AuthWebApplicationFactory();
        using var client = host.CreateHttpsClient();
        using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = "person@example.test", password = "test password" });
        await ErrorContractTests.AssertProblem(login, 503, "SERVICE_UNAVAILABLE");
        using var me = await client.GetAsync("/api/v1/auth/me");
        await ErrorContractTests.AssertProblem(me, 401, "AUTH_UNAUTHENTICATED");
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("public-only")]
    [InlineData("weak")]
    [InlineData("idle")]
    [InlineData("absolute")]
    [InlineData("issuer")]
    public async Task Startup_RejectsInvalidPrivateKeyOrSessionLimitsWithSafeError(string condition)
    {
        using var key = new TemporaryJwtKey();
        if (condition == "public-only") File.WriteAllText(key.Path, key.Rsa.ExportSubjectPublicKeyInfoPem());
        if (condition == "weak") { using var weak = RSA.Create(1024); File.WriteAllText(key.Path, weak.ExportPkcs8PrivateKeyPem()); }
        var path = condition == "missing" ? key.Path + ".missing" : key.Path;
        await using var host = new AuthWebApplicationFactory().WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Persistence:Enabled"] = "true", ["ConnectionStrings:DefaultConnection"] = "Server=local-test-only;Database=AuthProject_Test_Config;Integrated Security=True;",
                ["Auth:Jwt:Enabled"] = "true", ["Auth:Jwt:Issuer"] = condition == "issuer" ? "http://issuer.example.test" : key.Settings.Issuer,
                ["Auth:Jwt:Audience"] = key.Settings.Audience, ["Auth:Jwt:KeyId"] = key.Settings.KeyId, ["Auth:Jwt:SigningKeyPath"] = path,
                ["Auth:Jwt:SessionIdleLifetimeSeconds"] = condition == "idle" ? "0" : "604800",
                ["Auth:Jwt:SessionAbsoluteLifetimeSeconds"] = condition == "absolute" ? "1" : "2592000"
            })));
        var exception = Assert.Throws<OptionsValidationException>(() => host.CreateClient());
        Assert.DoesNotContain(key.Path, exception.Message);
        Assert.DoesNotContain("PRIVATE KEY", exception.Message);
    }
}
