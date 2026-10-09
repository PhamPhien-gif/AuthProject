using AuthProject.IntegrationTests.Database;
using AuthProject.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Extensions.Logging;

namespace AuthProject.IntegrationTests.Hosting;

public sealed class AuthWebApplicationFactory(string environment = "Testing", SqlServerFixture? database = null)
    : WebApplicationFactory<Program>
{
    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 10, 9, 0, 0, 0, TimeSpan.Zero));
    internal FakeEmailTransport Email => Services.GetRequiredService<FakeEmailTransport>();
    internal FakeGoogleBackchannel Google => Services.GetRequiredService<FakeGoogleBackchannel>();
    internal RecordingLoggerProvider Logs { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        // Keep test logging entirely in memory: no OS EventLog/files or external sinks.
        builder.ConfigureLogging(logging => logging.ClearProviders().AddProvider(Logs));
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                // Never inherit the developer's application database in this test host.
                ["ConnectionStrings:DefaultConnection"] = database?.ConnectionString ?? string.Empty
            }));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
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
