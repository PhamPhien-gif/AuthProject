using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace AuthProject.IntegrationTests.Foundation;

[Trait("Category", "Integration")]
[Trait("Dependency", "None")]
public sealed class OptionsStartupTests
{
    [Fact]
    public async Task Startup_WhenModulesConfigured_ValidatesWithoutActivatingAuthOrContactingProviders()
    {
        await using var factory = FoundationFactory.Create().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Auth:Jwt:Enabled"] = "true", ["Auth:Jwt:Issuer"] = "https://issuer.example.test",
                ["Auth:Jwt:Audience"] = "test-api", ["Auth:Jwt:SigningKeyPath"] = "test-only/key-reference.pem",
                ["Auth:Jwt:KeyId"] = "test-key",
                ["Auth:Email:Enabled"] = "true", ["Auth:Email:Host"] = "smtp.example.test",
                ["Auth:Email:Sender"] = "sender@example.test", ["Auth:Email:Username"] = "test",
                ["Auth:Email:Password"] = "test-only-credential",
                ["Auth:Google:Enabled"] = "true", ["Auth:Google:ClientId"] = "test-client",
                ["Auth:Google:ClientSecret"] = "test-only-credential",
                ["Auth:EmailOtp:Enabled"] = "true", ["Auth:EmailOtp:KeyId"] = "test-pepper",
                ["Auth:EmailOtp:Pepper"] = Convert.ToBase64String(new byte[32])
            })));
        using var client = factory.Client();
        using var response = await client.GetAsync("/api/v1/auth/me");
        await ErrorContractTests.AssertProblem(response, 404, "RESOURCE_NOT_FOUND");
    }

    [Theory]
    [InlineData("Api:MaxRequestBodyBytes", "0")]
    [InlineData("Api:RequestTimeoutSeconds", "0")]
    [InlineData("Auth:Jwt:Enabled", "true")]
    [InlineData("Auth:Email:Enabled", "true")]
    [InlineData("Auth:Google:Enabled", "true")]
    [InlineData("Auth:EmailOtp:Enabled", "true")]
    [InlineData("Persistence:Enabled", "true")]
    [InlineData("Security:PasswordHash:IterationCount", "1000")]
    [InlineData("BruteForce:RateLimit:MaxPartitions", "0")]
    [InlineData("BruteForce:RateLimit:Policies:Login:IpPermitLimit", "0")]
    [InlineData("BruteForce:Lockout:DurationSeconds", "0")]
    [InlineData("TrustedProxy:Enabled", "true")]
    public async Task Startup_WhenEnabledModuleOrApiLimitsInvalid_FailsWithSafeMessage(string key, string value)
    {
        await using var factory = FoundationFactory.Create().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?> { [key] = value, ["Auth:EmailOtp:Pepper"] = "DO_NOT_LEAK" })));
        var error = Assert.Throws<OptionsValidationException>(() => factory.Client());
        Assert.DoesNotContain("DO_NOT_LEAK", error.Message);
    }
}
