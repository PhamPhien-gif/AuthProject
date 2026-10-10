using AuthProject.IntegrationTests.Hosting;
using AuthProject.BackgroundJobs;
using AuthProject.TestSupport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace AuthProject.IntegrationTests.Foundation;

[Trait("Category", "Integration"), Trait("Dependency", "None")]
public sealed class MfaStartupTests
{
    [Theory]
    [InlineData("jwt")] [InlineData("refresh")] [InlineData("smtp")] [InlineData("pepper")]
    [InlineData("key")] [InlineData("ttl")] [InlineData("deadline")] [InlineData("attempts")]
    [InlineData("cooldown")] [InlineData("sends")] [InlineData("recent")] [InlineData("codes")] [InlineData("lease")]
    public async Task Enabled_MFA_rejects_unsafe_configuration_without_leaking_secrets(string state)
    {
        using var key = new TemporaryJwtKey();
        await using var root = new AuthWebApplicationFactory();
        await using var host = root.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Persistence:Enabled"] = "true", ["ConnectionStrings:DefaultConnection"] = "Server=local-test-only;Database=AuthProject_Test_Config;Integrated Security=True;",
                ["Auth:Jwt:Enabled"] = state == "jwt" ? "false" : "true", ["Auth:Jwt:Issuer"] = key.Settings.Issuer,
                ["Auth:Jwt:Audience"] = key.Settings.Audience, ["Auth:Jwt:KeyId"] = key.Settings.KeyId, ["Auth:Jwt:SigningKeyPath"] = key.Path,
                ["Auth:Refresh:Enabled"] = state == "refresh" ? "false" : "true", ["Auth:Refresh:AllowedOrigins:0"] = "https://localhost",
                ["Auth:Email:Enabled"] = state == "smtp" ? "false" : "true", ["Auth:Email:Host"] = "smtp.example.test",
                ["Auth:Email:Sender"] = "sender@example.test", ["Auth:Email:Username"] = "test", ["Auth:Email:Password"] = "DO_NOT_LEAK_SECRET",
                ["Auth:EmailOtp:Enabled"] = "true", ["Auth:EmailOtp:KeyId"] = state == "key" ? new string('x', 65) : "test",
                ["Auth:EmailOtp:Pepper"] = state == "pepper" ? "DO_NOT_LEAK_SECRET" : Convert.ToBase64String(new byte[32]),
                ["Auth:EmailOtp:ExpirySeconds"] = state == "ttl" ? "99999" : "300",
                ["Auth:EmailOtp:ChallengeLifetimeSeconds"] = state == "deadline" ? "99999" : "600",
                ["Auth:EmailOtp:MaxAttempts"] = state == "attempts" ? "99999" : "5",
                ["Auth:EmailOtp:ResendCooldownSeconds"] = state == "cooldown" ? "0" : "60",
                ["Auth:EmailOtp:MaxSends"] = state == "sends" ? "99999" : "3",
                ["Auth:EmailOtp:RecentAuthenticationSeconds"] = state == "recent" ? "99999" : "300",
                ["Auth:EmailOtp:RecoveryCodeCount"] = state == "codes" ? "99999" : "10",
                ["Auth:Registration:LeaseSeconds"] = state == "lease" ? "1" : "120"
            }));
            builder.ConfigureServices(services =>
            { foreach (var d in services.Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(MfaEmailWorker)).ToArray()) services.Remove(d); });
        });
        var ex = Assert.ThrowsAny<Exception>(() => host.CreateClient());
        if (ex is AggregateException aggregate) Assert.All(aggregate.Flatten().InnerExceptions, e => Assert.IsType<OptionsValidationException>(e));
        else Assert.IsType<OptionsValidationException>(ex);
        Assert.DoesNotContain("DO_NOT_LEAK_SECRET", ex.Message);
    }
}
