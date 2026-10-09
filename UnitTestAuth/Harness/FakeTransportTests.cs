using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AuthProject.TestSupport;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.Tokens;

namespace UnitTestAuth.Harness;

[Trait("Category", "Unit")]
public sealed class FakeTransportTests
{
    [Fact]
    public async Task Email_WhenSent_CapturesMessageWithoutNetwork()
    {
        var transport = new FakeEmailTransport();
        var email = new TestEmail("user@example.test", "Verify", "test-code");
        await transport.SendAsync(email);
        Assert.Equal(email, Assert.Single(transport.Sent));
    }

    [Fact]
    public async Task Email_WhenDeliveryFailsOrIsCancelled_DoesNotRecordSuccess()
    {
        var transport = new FakeEmailTransport { Failure = new IOException("Simulated SMTP outage") };
        var email = new TestEmail("user@example.test", "Verify", "test-code");
        await Assert.ThrowsAsync<IOException>(() => transport.SendAsync(email));
        transport.Failure = null;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transport.SendAsync(email, new CancellationToken(true)));
        Assert.Empty(transport.Sent);
    }

    [Fact]
    public async Task Google_WhenCodeIsRedeemed_ReturnsSignedClaimsBoundToNonceAndFakeTime()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 9, 0, 0, 0, TimeSpan.Zero));
        using var handler = new FakeGoogleBackchannel(clock);
        using var client = new HttpClient(handler);
        var identity = new TestIdentityBuilder().WithSubject("google-subject-123").Build();
        var code = handler.IssueCode(identity, "expected-nonce");
        using var response = await client.PostAsync(FakeGoogleBackchannel.Issuer + "/token", TokenRequest(code));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var payload = await response.Content.ReadFromJsonAsync<JsonDocument>();
        var jwt = payload!.RootElement.GetProperty("id_token").GetString()!;
        var jwks = new JsonWebKeySet(await client.GetStringAsync(FakeGoogleBackchannel.Issuer + "/keys"));
        var principal = new JwtSecurityTokenHandler { MapInboundClaims = false }.ValidateToken(jwt,
            new TokenValidationParameters
            {
                ValidIssuer = FakeGoogleBackchannel.Issuer,
                ValidAudience = FakeGoogleBackchannel.ClientId,
                IssuerSigningKeys = jwks.GetSigningKeys(),
                ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                ValidateLifetime = true,
                LifetimeValidator = (notBefore, expires, _, _) =>
                    notBefore <= clock.GetUtcNow().UtcDateTime && expires > clock.GetUtcNow().UtcDateTime
            }, out _);

        Assert.Equal(identity.Subject, principal.FindFirst("sub")!.Value);
        Assert.Equal(identity.Email, principal.FindFirst("email")!.Value);
        Assert.Equal("expected-nonce", principal.FindFirst("nonce")!.Value);
        Assert.Equal(clock.GetUtcNow().AddMinutes(5).UtcDateTime, new JwtSecurityToken(jwt).ValidTo);
        clock.Advance(TimeSpan.FromMinutes(6));
        Assert.True(new JwtSecurityToken(jwt).ValidTo < clock.GetUtcNow().UtcDateTime);

        using var replay = await client.PostAsync(FakeGoogleBackchannel.Issuer + "/token", TokenRequest(code));
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
    }

    [Fact]
    public async Task Google_WhenMetadataRequested_UsesFakeEndpointsAndRejectsUnexpectedNetwork()
    {
        using var client = new HttpClient(new FakeGoogleBackchannel(new FakeTimeProvider()));
        using var discovery = await client.GetFromJsonAsync<JsonDocument>(
            FakeGoogleBackchannel.Issuer + "/.well-known/openid-configuration");
        Assert.Equal(FakeGoogleBackchannel.Issuer, discovery!.RootElement.GetProperty("issuer").GetString());
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync("https://accounts.google.com/real-network"));
    }

    private static FormUrlEncodedContent TokenRequest(string code) => new(new Dictionary<string, string>
    {
        ["grant_type"] = "authorization_code",
        ["client_id"] = FakeGoogleBackchannel.ClientId,
        ["code"] = code
    });
}
