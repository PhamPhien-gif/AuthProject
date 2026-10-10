using System.Security.Claims;
using System.Globalization;

namespace AuthProject.Security.Authentication;

public static class AccessTokenRules
{
    public static bool Lifetime(DateTime? notBefore, DateTime? expires, DateTimeOffset now, int skewSeconds)
        => notBefore is not null && expires is not null && notBefore < expires
            && notBefore <= now.UtcDateTime.AddSeconds(skewSeconds) && expires > now.UtcDateTime.AddSeconds(-skewSeconds);
    public static bool TryRead(ClaimsPrincipal principal, out Guid userId, out Guid sessionId, out long authVersion)
    {
        userId = sessionId = Guid.Empty;
        authVersion = 0;
        foreach (var name in new[] { "sub", "sid", "av", "jti", "iat" })
            if (principal.FindAll(name).Count() != 1) return false;
        var methods = principal.FindAll("amr").Select(c => c.Value).ToArray();
        if (methods.Count(m => m == "pwd") != 1 || methods.Count(m => m == "mfa") > 1
            || methods.Any(m => m is not ("pwd" or "mfa"))) return false;
        return Guid.TryParse(principal.FindFirst("sub")!.Value, out userId) && userId != Guid.Empty
            && Guid.TryParse(principal.FindFirst("sid")!.Value, out sessionId) && sessionId != Guid.Empty
            && long.TryParse(principal.FindFirst("av")!.Value, NumberStyles.None, CultureInfo.InvariantCulture, out authVersion) && authVersion >= 1
            && Guid.TryParse(principal.FindFirst("jti")!.Value, out var jti) && jti != Guid.Empty
            && long.TryParse(principal.FindFirst("iat")!.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var iat) && iat >= 0;
    }
}
