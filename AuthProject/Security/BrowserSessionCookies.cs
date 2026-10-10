using AuthProject.Configuration;
using Microsoft.Extensions.Options;

namespace AuthProject.Security;

public sealed class BrowserSessionCookies(IOptions<RefreshOptions> options)
{
    public const string Name = "__Secure-AuthProject.Refresh";
    public const string Path = "/api/v1/auth";
    public const string CsrfHeader = "X-CSRF-TOKEN";
    private static CookieOptions Flags() => new() { Path = Path, Secure = true, HttpOnly = true, SameSite = SameSiteMode.Strict, IsEssential = true };
    public void Write(HttpResponse response, SessionGrant grant)
    {
        if (!options.Value.Enabled) return;
        var flags = Flags();
        flags.Expires = grant.ExpiresAt;
        response.Cookies.Append(Name, grant.RefreshToken, flags);
    }
    public void Clear(HttpResponse response) => response.Cookies.Delete(Name, Flags());
}
