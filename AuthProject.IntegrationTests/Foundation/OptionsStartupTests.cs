using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using AuthProject.BackgroundJobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AuthProject.IntegrationTests.Foundation;

[Trait("Category", "Integration")]
[Trait("Dependency", "None")]
public sealed class OptionsStartupTests
{
    [Fact]
    public async Task Startup_WhenModulesConfigured_ValidatesKeyWithoutContactingProviders()
    {
        using var key = new AuthProject.TestSupport.TemporaryJwtKey();
        await using var factory = FoundationFactory.Create().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Auth:Jwt:Enabled"] = "true", ["Auth:Jwt:Issuer"] = "https://issuer.example.test",
                ["Auth:Jwt:Audience"] = "test-api", ["Auth:Jwt:SigningKeyPath"] = key.Path,
                ["Auth:Jwt:KeyId"] = "test-key", ["Persistence:Enabled"] = "true",
                ["Auth:Refresh:Enabled"] = "true", ["Auth:Refresh:AllowedOrigins:0"] = "https://localhost",
                ["ConnectionStrings:DefaultConnection"] = "Server=local-test-only;Database=AuthProject_Test_Config;Integrated Security=True;",
                ["Auth:Email:Enabled"] = "true", ["Auth:Email:Host"] = "smtp.example.test",
                ["Auth:Email:Sender"] = "sender@example.test", ["Auth:Email:Username"] = "test",
                ["Auth:Email:Password"] = "test-only-credential",
                ["Auth:Google:Enabled"] = "true", ["Auth:Google:ClientId"] = "test-client",
                ["Auth:Google:ClientSecret"] = "test-only-credential",
                ["Auth:Google:CallbackUri"] = "https://localhost/signin-google",
                ["Auth:Google:CompletionUrl"] = "https://localhost/google-complete", ["Auth:Google:ErrorUrl"] = "https://localhost/google-error",
                ["Auth:EmailOtp:Enabled"] = "true", ["Auth:EmailOtp:KeyId"] = "test-pepper",
                ["Auth:EmailOtp:Pepper"] = Convert.ToBase64String(new byte[32])
            }));
            builder.ConfigureServices(services =>
            {
                foreach (var d in services.Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(MfaEmailWorker)).ToArray()) services.Remove(d);
            });
        });
        using var client = factory.Client();
        using var response = await client.GetAsync("/api/v1/auth/me");
        await ErrorContractTests.AssertProblem(response, 401, "AUTH_UNAUTHENTICATED");
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
    [InlineData("Auth:Registration:Enabled", "true")]
    public async Task Startup_WhenEnabledModuleOrApiLimitsInvalid_FailsWithSafeMessage(string key, string value)
    {
        await using var factory = FoundationFactory.Create().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?> { [key] = value, ["Auth:EmailOtp:Pepper"] = "DO_NOT_LEAK" })));
        var error = Assert.Throws<OptionsValidationException>(() => factory.Client());
        Assert.DoesNotContain("DO_NOT_LEAK", error.Message);
    }
}
