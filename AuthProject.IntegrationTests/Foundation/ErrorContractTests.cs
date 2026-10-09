using System.Net;
using System.Text;
using System.Text.Json;

namespace AuthProject.IntegrationTests.Foundation;

[Trait("Category", "Integration")]
[Trait("Dependency", "None")]
public sealed class ErrorContractTests
{
    [Theory]
    [InlineData(401, "AUTH_UNAUTHENTICATED")]
    [InlineData(403, "AUTH_FORBIDDEN")]
    [InlineData(404, "RESOURCE_NOT_FOUND")]
    [InlineData(429, "RATE_LIMITED")]
    [InlineData(503, "SERVICE_UNAVAILABLE")]
    public async Task Status_WhenEmpty_UsesSharedContractAndPreservesHeaders(int status, string code)
    {
        await using var factory = FoundationFactory.Create();
        using var client = factory.Client();
        using var response = await client.GetAsync($"/_tests/foundation/status/{status}?secret=DO_NOT_LEAK");
        await AssertProblem(response, status, code);
        if (status == 401) Assert.Equal("Bearer", response.Headers.WwwAuthenticate.Single().Scheme);
        if (status == 429) Assert.Equal(TimeSpan.FromSeconds(60), response.Headers.RetryAfter!.Delta);
    }

    [Theory]
    [InlineData("{", "application/json", 400)]
    [InlineData("null", "application/json", 400)]
    [InlineData("", "application/json", 400)]
    [InlineData("{}", "text/plain", 415)]
    [InlineData("{\"email\":\"a@b.com\",\"password\":\"123456789012345\",\"displayName\":\"User\",\"role\":\"Admin\"}", "application/json", 400)]
    public async Task Binding_WhenInvalid_UsesSafeContract(string body, string contentType, int status)
    {
        await using var factory = FoundationFactory.Create();
        using var client = factory.Client();
        using var response = await client.PostAsync("/_tests/foundation/register", new StringContent(body, Encoding.UTF8, contentType));
        await AssertProblem(response, status, status == 415 ? "UNSUPPORTED_MEDIA_TYPE" : "INVALID_REQUEST");
    }

    [Fact]
    public async Task Validation_WhenSeveralFieldsInvalid_ReturnsFieldCodes()
    {
        await using var factory = FoundationFactory.Create();
        using var client = factory.Client();
        using var response = await client.PostAsync("/_tests/foundation/register",
            new StringContent("{\"email\":\"bad\",\"password\":\"x\",\"displayName\":\"\"}", Encoding.UTF8, "application/json"));
        var problem = await AssertProblem(response, 400, "VALIDATION_FAILED");
        Assert.Equal("EMAIL_INVALID", problem.GetProperty("errors").GetProperty("email")[0].GetProperty("code").GetString());
        Assert.True(problem.GetProperty("errors").TryGetProperty("password", out _));
        Assert.True(problem.GetProperty("errors").TryGetProperty("displayName", out _));
    }

    [Theory]
    [InlineData("Testing")]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task Exception_InAnyEnvironment_IsSanitizedEvenForHtmlAccept(string environment)
    {
        await using var factory = FoundationFactory.Create(environment);
        using var client = factory.Client();
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html");
        using var response = await client.GetAsync("/_tests/foundation/failure?secret=DO_NOT_LEAK");
        await AssertProblem(response, 500, "INTERNAL_ERROR");
    }

    [Fact]
    public async Task MethodAndRoute_WhenUnknown_ReturnContractAndAllowHeader()
    {
        await using var factory = FoundationFactory.Create();
        using var client = factory.Client();
        using var missing = await client.GetAsync("/unknown-route");
        await AssertProblem(missing, 404, "RESOURCE_NOT_FOUND");
        using var method = await client.GetAsync("/_tests/foundation/register");
        await AssertProblem(method, 405, "METHOD_NOT_ALLOWED");
        Assert.Contains("POST", method.Content.Headers.Allow);
        using var query = await client.GetAsync("/_tests/foundation/query?page=DO_NOT_LEAK");
        await AssertProblem(query, 400, "INVALID_REQUEST");
    }

    [Fact]
    public async Task Body_WhenTooLarge_Returns413()
    {
        await using var factory = FoundationFactory.Create();
        using var client = factory.Client();
        using var response = await client.PostAsync("/_tests/foundation/register", new StringContent(new string('a', 17000), Encoding.UTF8, "application/json"));
        await AssertProblem(response, 413, "REQUEST_TOO_LARGE");
    }

    internal static async Task<JsonElement> AssertProblem(HttpResponseMessage response, int status, string code)
    {
        Assert.Equal((HttpStatusCode)status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("DO_NOT_LEAK", body);
        Assert.DoesNotContain("stackTrace", body, StringComparison.OrdinalIgnoreCase);
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        Assert.Equal(status, root.GetProperty("status").GetInt32());
        Assert.Equal(code, root.GetProperty("code").GetString());
        Assert.StartsWith("urn:authproject:problem:", root.GetProperty("type").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("title").GetString()));
        Assert.DoesNotContain("?", root.GetProperty("instance").GetString()!);
        Assert.Equal(response.Headers.GetValues("X-Trace-Id").Single(), root.GetProperty("traceId").GetString());
        return root.Clone();
    }
}
