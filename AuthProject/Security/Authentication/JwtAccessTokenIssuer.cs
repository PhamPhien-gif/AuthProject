using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using AuthProject.Configuration;
using AuthProject.Entities;
using AuthProject.Security.Interfaces;
using Microsoft.Extensions.Options;

namespace AuthProject.Security.Authentication;

public sealed class JwtAccessTokenIssuer(JwtSigningKey key, IOptions<JwtOptions> options, TimeProvider clock) : IAccessTokenIssuer
{
    private readonly object sync = new();
    public string Issue(AuthSession session)
    {
        var settings = options.Value;
        var now = clock.GetUtcNow();
        var claims = new List<Claim>
        {
            new("sub", session.UserId.ToString()), new("sid", session.Id.ToString()),
            new("av", session.AuthVersion.ToString(CultureInfo.InvariantCulture)), new("jti", Guid.NewGuid().ToString()),
            new("iat", now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), ClaimValueTypes.Integer64),
            new("amr", session.PrimaryMethod == AuthenticationMethod.Google ? "federated" : "pwd")
        };
        // Generic MFA evidence covers either email OTP or a one-use recovery code.
        if (session.MfaVerifiedAt is not null) claims.Add(new("amr", "mfa"));
        var token = new JwtSecurityToken(settings.Issuer, settings.Audience,
            claims,
            now.UtcDateTime, DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds()
                + SessionLifetimeRules.AccessLifetime(session, now, settings.AccessTokenLifetimeSeconds)).UtcDateTime, key.Credentials);
        token.Header["typ"] = "at+jwt";
        lock (sync) return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
