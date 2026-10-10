using AuthProject.IntegrationTests.Database;
using AuthProject.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.DataProtection;

namespace AuthProject.IntegrationTests.Hosting;

public sealed class AuthWebApplicationFactory(string environment = "Testing", SqlServerFixture? database = null, string? contentRoot = null)
    : WebApplicationFactory<Program>
{
    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 10, 9, 0, 0, 0, TimeSpan.Zero));
    internal FakeEmailTransport Email => Services.GetRequiredService<FakeEmailTransport>();
    internal FakeGoogleBackchannel Google => Services.GetRequiredService<FakeGoogleBackchannel>();
    internal RecordingLoggerProvider Logs { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        if (contentRoot is not null) builder.UseContentRoot(contentRoot);
        // Keep test logging entirely in memory: no OS EventLog/files or external sinks.
        builder.ConfigureLogging(logging => logging.ClearProviders().AddProvider(Logs));
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                // Never inherit the developer's application database in this test host.
                ["ConnectionStrings:DefaultConnection"] = database?.ConnectionString ?? string.Empty,
                ["Persistence:Enabled"] = "false",
                ["Audit:Log4Net:Enabled"] = "false",
                ["Auth:Jwt:Enabled"] = "false",
                ["Auth:Refresh:Enabled"] = "false",
                ["Auth:Registration:Enabled"] = "false",
                ["Auth:Email:Enabled"] = "false",
                ["Auth:Google:Enabled"] = "false",
                ["Auth:EmailOtp:Enabled"] = "false"
            }));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
            // Isolate key material and avoid key files in all test hosts.
            services.RemoveAll<IDataProtectionProvider>();
            services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
            services.AddSingleton<FakeEmailTransport>();
            services.AddSingleton(_ => new FakeGoogleBackchannel(Clock));
        });
    }

    public HttpClient CreateHttpsClient() => CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"),
        AllowAutoRedirect = false
    });
}
