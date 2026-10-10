using AuthProject.IntegrationTests.Hosting;
using AuthProject.BackgroundJobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace AuthProject.IntegrationTests.Foundation;

[Trait("Category", "Integration"), Trait("Dependency", "None")]
public sealed class PasswordRecoveryStartupTests
{
    [Theory]
    [InlineData("persistence")]
    [InlineData("smtp")]
    [InlineData("http")]
    [InlineData("query")]
    [InlineData("ttl")]
    [InlineData("recent")]
    [InlineData("lease")]
    public async Task EnabledRecovery_RejectsUnsafeOptionsAtStartup(string state)
    {
        await using var host = new AuthWebApplicationFactory().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Persistence:Enabled"] = state == "persistence" ? "false" : "true",
                ["ConnectionStrings:DefaultConnection"] = "Server=local-test-only;Database=AuthProject_Test_Config;Integrated Security=True;",
                ["Auth:Email:Enabled"] = state == "smtp" ? "false" : "true", ["Auth:Email:Host"] = "smtp.example.test",
                ["Auth:Email:Sender"] = "sender@example.test", ["Auth:Email:Username"] = "test-user", ["Auth:Email:Password"] = "DO_NOT_LEAK_SECRET",
                ["Auth:PasswordRecovery:Enabled"] = "true", ["Auth:PasswordRecovery:ResetUrl"] = state == "http" ? "http://client.example.test/reset" : state == "query" ? "https://client.example.test/reset?bad=1" : "https://client.example.test/reset",
                ["Auth:PasswordRecovery:TokenLifetimeSeconds"] = state == "ttl" ? "999999" : "1800",
                ["Auth:PasswordRecovery:RecentAuthenticationSeconds"] = state == "recent" ? "999999" : "300",
                ["Auth:Registration:LeaseSeconds"] = state == "lease" ? "1" : "120"
            }));
            builder.ConfigureServices(services => { foreach (var d in services.Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(PasswordEmailWorker)).ToArray()) services.Remove(d); });
        });
        var ex = Assert.Throws<OptionsValidationException>(() => host.CreateClient()); Assert.DoesNotContain("DO_NOT_LEAK_SECRET", ex.Message);
    }
}
