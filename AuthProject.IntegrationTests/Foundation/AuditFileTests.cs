using AuthProject.Entities;
using AuthProject.IntegrationTests.Hosting;
using AuthProject.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AuthProject.IntegrationTests.Foundation;

[Trait("Category", "Integration"), Trait("Dependency", "None")]
public sealed class AuditFileTests
{
    [Fact]
    public async Task Actual_rolling_file_writes_JSON_and_reports_locked_file_instead_of_silent_success()
    {
        var directory = Path.Combine(Path.GetTempPath(), "AuthProject_Test_File_" + Guid.NewGuid().ToString("N"));
        await using var root = new AuthWebApplicationFactory();
        await using (var host = root.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string,string?>
        { ["Audit:Log4Net:Enabled"] = "true", ["Audit:Log4Net:Directory"] = directory, ["Audit:Log4Net:InstanceId"] = "test" }))))
        {
            using var client = host.CreateClient(new() { BaseAddress = new Uri("https://localhost") }); var sink = host.Services.GetRequiredService<IAuditSink>();
            var envelope = new AuditEnvelope(Guid.NewGuid(), DateTimeOffset.UtcNow, new(AuditEventType.LoginCompleted, AuditOutcome.Success, AuditReason.None, "safe_trace"));
            sink.Write(envelope); var path = Path.Combine(directory, "audit-test.jsonl");
            Assert.Contains(envelope.EventId.ToString(), await File.ReadAllTextAsync(path));
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) Assert.Throws<IOException>(() => sink.Write(envelope));
            sink.Write(envelope);
        }
        foreach (var path in Directory.EnumerateFiles(directory)) File.Delete(path); Directory.Delete(directory);
    }
    [Theory]
    [InlineData("delivery")] [InlineData("retention")] [InlineData("file")]
    public async Task Unsafe_audit_options_fail_startup_without_touching_database(string state)
    {
        await using var root = new AuthWebApplicationFactory();
        await using var host = root.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string,string?>
        {
            ["Audit:Delivery:Enabled"] = state == "delivery" ? "true" : "false", ["Audit:Retention:Enabled"] = state == "retention" ? "true" : "false",
            ["Audit:Log4Net:Enabled"] = state == "file" ? "true" : "false", ["Audit:Log4Net:InstanceId"] = "../unsafe"
        })));
        var error = Assert.ThrowsAny<Exception>(() => host.CreateClient());
        if (error is AggregateException many) Assert.All(many.Flatten().InnerExceptions, e => Assert.IsType<OptionsValidationException>(e));
        else Assert.IsType<OptionsValidationException>(error);
    }
}
