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
        var token = new JwtSecurityToken(settings.Issuer, settings.Audience,
            [new Claim("sub", session.UserId.ToString()), new Claim("sid", session.Id.ToString()),
             new Claim("av", session.AuthVersion.ToString(CultureInfo.InvariantCulture)), new Claim("jti", Guid.NewGuid().ToString()),
             new Claim("iat", now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), ClaimValueTypes.Integer64), new Claim("amr", "pwd")],
            now.UtcDateTime, now.AddSeconds(settings.AccessTokenLifetimeSeconds).UtcDateTime, key.Credentials);
        token.Header["typ"] = "at+jwt";
        lock (sync) return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
