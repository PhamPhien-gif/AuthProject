using AuthProject.Data;
using AuthProject.Entities;
using AuthProject.IntegrationTests.Hosting;
using AuthProject.Logging;
using AuthProject.Repositories.Interfaces;
using AuthProject.Security.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AuthProject.IntegrationTests.Foundation;

[Trait("Category", "Integration")]
[Trait("Dependency", "None")]
public sealed class PersistenceStartupTests
{
    [Fact]
    public async Task Startup_WhenPersistenceEnabled_ResolvesSharedStoreAndStagesAuditWithoutOpeningSql()
    {
        await using var factory = new AuthWebApplicationFactory().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Persistence:Enabled"] = "true",
                ["ConnectionStrings:DefaultConnection"] = "Server=unreachable.example.test;Database=TestOnly;Integrated Security=True;Encrypt=True"
            })));
        using var client = factory.Client();
        using var scope = factory.Services.CreateScope();
        var services = scope.ServiceProvider;
        var context = services.GetRequiredService<AuthDbContext>();
        Assert.Equal("Microsoft.EntityFrameworkCore.SqlServer", context.Database.ProviderName);
        Assert.Equal(System.Data.ConnectionState.Closed, context.Database.GetDbConnection().State);
        Assert.IsAssignableFrom<IAuthUnitOfWork>(services.GetRequiredService<IAuthUnitOfWork>());
        services.GetRequiredService<IUserRepository>().Add(new User { Email = "test@example.test", DisplayName = "Test" });
        services.GetRequiredService<IAuditWriter>().Stage(new(AuditEventType.Registered, AuditOutcome.Success, AuditReason.None, "trace_123"));
        Assert.Single(context.ChangeTracker.Entries<User>());
        Assert.Single(context.ChangeTracker.Entries<AuditEvent>());
        Assert.Single(context.ChangeTracker.Entries<OutboxMessage>());
        Assert.Equal(System.Data.ConnectionState.Closed, context.Database.GetDbConnection().State);
        var hasher = services.GetRequiredService<IPasswordHashService>();
        var user = new User();
        Assert.Equal(PasswordCheck.Success, hasher.Verify(user, hasher.Hash(user, "a sufficiently long password"), "a sufficiently long password"));
    }

    [Fact]
    public async Task Startup_WhenPersistenceDisabled_RejectsStoreAccessAndDoesNotContactSql()
    {
        await using var factory = new AuthWebApplicationFactory();
        using var client = factory.CreateHttpsClient();
        using var scope = factory.Services.CreateScope();
        Assert.Throws<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<AuthDbContext>());
    }
}
