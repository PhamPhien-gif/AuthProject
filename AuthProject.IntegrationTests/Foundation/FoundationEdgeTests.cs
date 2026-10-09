using System.Net;
using System.Text;
using AuthProject.IntegrationTests.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AuthProject.IntegrationTests.Foundation;

[Trait("Category", "Integration")]
[Trait("Dependency", "None")]
public sealed class FoundationEdgeTests
{
    [Fact]
    public async Task FrameworkProblemService_WhenGivenUnsafeDetail_UsesSanitizedWriter()
    {
        await using var factory = FoundationFactory.Create();
        using var client = factory.Client();
        using var response = await client.GetAsync("/_tests/foundation/framework-problem");
        await ErrorContractTests.AssertProblem(response, 400, "INVALID_REQUEST");
    }

    [Fact]
    public async Task ExplicitErrorBody_WhenControllerReturnsIt_StillUsesSafeContract()
    {
        await using var factory = FoundationFactory.Create();
        using var client = factory.Client();
        using var response = await client.GetAsync("/_tests/foundation/body-error");
        await ErrorContractTests.AssertProblem(response, 400, "INVALID_REQUEST");
    }

    [Fact]
    public async Task ExpectedException_WhenCatalogCodeKnown_ReturnsMappedConflict()
    {
        await using var factory = FoundationFactory.Create();
        using var client = factory.Client();
        using var response = await client.GetAsync("/_tests/foundation/expected-error");
        await ErrorContractTests.AssertProblem(response, 409, "RESOURCE_CONFLICT");
    }

    [Theory]
    [InlineData("allowed", 204)]
    [InlineData("denied", 400)]
    public async Task Validation_WhenRuleIsAsync_IsAwaitedBeforeAction(string value, int expected)
    {
        await using var factory = FoundationFactory.Create();
        using var client = factory.Client();
        using var response = await client.PostAsync("/_tests/foundation/async", new StringContent(
            "{\"value\":\"" + value + "\"}", Encoding.UTF8, "application/json"));
        Assert.Equal((HttpStatusCode)expected, response.StatusCode);
        if (expected == 400) await ErrorContractTests.AssertProblem(response, 400, "VALIDATION_FAILED");
    }

    [Fact]
    public async Task UnknownLengthBody_WhenOversized_Returns413()
    {
        await using var factory = FoundationFactory.Create();
        using var client = factory.Client();
        using var content = new UnknownLengthContent(new string('a', 17000));
        using var response = await client.PostAsync("/_tests/foundation/register", content);
        await ErrorContractTests.AssertProblem(response, 413, "REQUEST_TOO_LARGE");
    }

    [Fact]
    public async Task Request_WhenTimeoutExpires_Returns503WithTrace()
    {
        await using var factory = FoundationFactory.Create().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?> { ["Api:RequestTimeoutSeconds"] = "1" })));
        using var client = factory.Client();
        using var response = await client.GetAsync("/_tests/foundation/timeout");
        await ErrorContractTests.AssertProblem(response, 503, "SERVICE_UNAVAILABLE");
    }

    [Fact]
    public async Task Failure_WhenLogged_DoesNotExposeExceptionMessageOrQuery()
    {
        await using var rootFactory = new AuthWebApplicationFactory();
        await using var factory = rootFactory.WithWebHostBuilder(builder => builder.ConfigureServices(
            services => services.AddControllers().AddApplicationPart(typeof(ProbeController).Assembly)));
        using var client = factory.Client();
        using var response = await client.GetAsync("/_tests/foundation/failure?token=DO_NOT_LEAK");
        await ErrorContractTests.AssertProblem(response, 500, "INTERNAL_ERROR");
        Assert.DoesNotContain(rootFactory.Logs.Entries, entry => entry.Contains("DO_NOT_LEAK", StringComparison.Ordinal));
        Assert.Contains(rootFactory.Logs.Entries, entry => entry.Contains("INTERNAL_ERROR", StringComparison.Ordinal)
            && entry.Contains(response.Headers.GetValues("X-Trace-Id").Single(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task RealHost_DoesNotDiscoverProbeController()
    {
        await using var factory = new AuthWebApplicationFactory("Development");
        using var client = factory.CreateHttpsClient();
        using var response = await client.GetAsync("/_tests/foundation/failure");
        await ErrorContractTests.AssertProblem(response, 404, "RESOURCE_NOT_FOUND");
        Assert.DoesNotContain("_tests", await client.GetStringAsync("/swagger/v1/swagger.json"));
    }

    private sealed class UnknownLengthContent(string value) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(Encoding.UTF8.GetBytes(value)).AsTask();
    }
}
