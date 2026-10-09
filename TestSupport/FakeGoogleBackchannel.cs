using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.IdentityModel.Tokens;

namespace AuthProject.TestSupport;

internal sealed class FakeGoogleBackchannel(TimeProvider clock) : HttpMessageHandler
{
    public const string Issuer = "https://accounts.google.test";
    public const string ClientId = "authproject-test-client";
    private readonly RSA rsa = RSA.Create(2048);
    private readonly string keyId = Guid.NewGuid().ToString("N");
    private readonly ConcurrentDictionary<string, (TestIdentity Identity, string Nonce, DateTimeOffset ExpiresAt)> codes = new();

    public string IssueCode(TestIdentity identity, string nonce)
    {
        var code = Guid.NewGuid().ToString("N");
        codes[code] = (identity, nonce, clock.GetUtcNow().AddMinutes(1));
        return code;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.RequestUri?.GetLeftPart(UriPartial.Authority) != Issuer)
            throw new InvalidOperationException("Unexpected OIDC request: real network access is forbidden in this fake.");

        if (request.Method == HttpMethod.Get && request.RequestUri.AbsolutePath == "/.well-known/openid-configuration")
        {
            return Json(HttpStatusCode.OK, new
            {
                issuer = Issuer,
                authorization_endpoint = Issuer + "/authorize",
                token_endpoint = Issuer + "/token",
                jwks_uri = Issuer + "/keys",
                response_types_supported = new[] { "code" },
                subject_types_supported = new[] { "public" },
                id_token_signing_alg_values_supported = new[] { "RS256" }
            });
        }

        if (request.Method == HttpMethod.Get && request.RequestUri.AbsolutePath == "/keys")
        {
            var key = rsa.ExportParameters(false);
            return Json(HttpStatusCode.OK, new
            {
                keys = new[] { new { kty = "RSA", use = "sig", alg = "RS256", kid = keyId,
                    n = Base64UrlEncoder.Encode(key.Modulus!), e = Base64UrlEncoder.Encode(key.Exponent!) } }
            });
        }

        if (request.Method == HttpMethod.Post && request.RequestUri.AbsolutePath == "/token")
        {
            var form = QueryHelpers.ParseQuery(request.Content is null
                ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            if (form["grant_type"] != "authorization_code" || form["client_id"] != ClientId
                || !codes.TryRemove(form["code"].ToString(), out var grant) || grant.ExpiresAt <= clock.GetUtcNow())
                return Json(HttpStatusCode.BadRequest, new { error = "invalid_grant" });

            var now = clock.GetUtcNow().UtcDateTime;
            var token = new JwtSecurityToken(Issuer, ClientId,
            [
                new Claim("sub", grant.Identity.Subject),
                new Claim("email", grant.Identity.Email),
                new Claim("name", grant.Identity.DisplayName),
                new Claim("email_verified", grant.Identity.EmailVerified ? "true" : "false", ClaimValueTypes.Boolean),
                new Claim("nonce", grant.Nonce),
                new Claim("iat", EpochTime.GetIntDate(now).ToString(System.Globalization.CultureInfo.InvariantCulture), ClaimValueTypes.Integer64)
            ], now, now.AddMinutes(5), new SigningCredentials(new RsaSecurityKey(rsa) { KeyId = keyId }, SecurityAlgorithms.RsaSha256));

            return Json(HttpStatusCode.OK, new
            {
                token_type = "Bearer", expires_in = 300,
                access_token = "test-only-provider-access-token",
                id_token = new JwtSecurityTokenHandler().WriteToken(token)
            });
        }

        throw new InvalidOperationException("Unexpected OIDC endpoint or HTTP method in test backchannel.");
    }

    private static HttpResponseMessage Json(HttpStatusCode status, object payload)
        => new(status) { Content = JsonContent.Create(payload) };

    protected override void Dispose(bool disposing)
    {
        if (disposing) rsa.Dispose();
        base.Dispose(disposing);
    }
}

