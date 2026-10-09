using AuthProject.Configuration;
using AuthProject.IntegrationTests.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AuthProject.IntegrationTests.Foundation;

[Trait("Category", "Integration")]
[Trait("Dependency", "None")]
public sealed class DotEnvStartupTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "AuthProject_DotEnvHost_" + Guid.NewGuid().ToString("N"));

    public DotEnvStartupTests()
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "AuthProject.csproj"), "test marker");
        File.WriteAllText(Path.Combine(root, ".env"), "Api__MaxRequestBodyBytes=12288\n");
    }

    [Theory]
    [InlineData("Development", 12288)]
    [InlineData("Testing", 16384)]
    [InlineData("Production", 16384)]
    public async Task Startup_LoadsDotEnvOnlyInDevelopmentBeforeOptionsValidation(string environment, int expectedLimit)
    {
        await using var factory = new AuthWebApplicationFactory(environment, contentRoot: root);
        using var client = factory.CreateHttpsClient();
        Assert.Equal(expectedLimit, factory.Services.GetRequiredService<IOptions<ApiOptions>>().Value.MaxRequestBodyBytes);
    }

    public void Dispose()
    {
        var target = Path.GetFullPath(root);
        if (!target.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(target).StartsWith("AuthProject_DotEnvHost_", StringComparison.Ordinal))
            throw new InvalidOperationException("Unexpected test temporary directory.");
        Directory.Delete(target, recursive: true);
    }
}
