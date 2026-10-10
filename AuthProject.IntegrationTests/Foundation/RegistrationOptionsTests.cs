using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace AuthProject.IntegrationTests.Foundation;

[Trait("Category", "Integration")]
[Trait("Dependency", "None")]
public sealed class RegistrationOptionsTests
{
    [Theory]
    [InlineData("VerificationUrl", "http://client.example.test/verify")]
    [InlineData("VerificationUrl", "https://client.example.test/verify?token=secret")]
    [InlineData("VerificationUrl", "https://user:secret@client.example.test/verify")]
    [InlineData("TokenLifetimeSeconds", "0")]
    [InlineData("MaxSendsPerWindow", "0")]
    [InlineData("ResendCooldownSeconds", "0")]
    [InlineData("LeaseSeconds", "30")]
    [InlineData("PollSeconds", "0")]
    public async Task InvalidRegistrationOptions_FailStartupWithoutEchoingConfig(string key, string value)
    {
        await using var factory = FoundationFactory.Create().WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Persistence:Enabled"] = "true",
                ["ConnectionStrings:DefaultConnection"] = "Server=local-test-only;Database=AuthProject_Test_Config;Integrated Security=True;",
                ["Auth:Email:Enabled"] = "true", ["Auth:Email:Host"] = "smtp.example.test",
                ["Auth:Email:Sender"] = "sender@example.test", ["Auth:Email:Username"] = "test", ["Auth:Email:Password"] = "secret",
                ["Auth:Registration:Enabled"] = "true", ["Auth:Registration:VerificationUrl"] = "https://client.example.test/verify",
                ["Auth:Registration:" + key] = value
            })));
        var error = Assert.Throws<OptionsValidationException>(() => factory.Client());
        Assert.DoesNotContain("secret", error.Message);
    }
}
