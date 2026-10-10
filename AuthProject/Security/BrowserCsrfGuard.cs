using System.Security.Claims;
using AuthProject.Configuration;
using AuthProject.Exceptions;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.Options;

namespace AuthProject.Security;

public sealed class BrowserCsrfGuard(IAntiforgery antiforgery, IOptions<RefreshOptions> options)
{
    public void Origin(HttpContext context)
    {
        var values = context.Request.Headers.Origin;
        string? origin = values.Count == 1 ? values[0] : null;
        if (values.Count == 0 && context.Request.Headers.Referer.Count == 1
            && Uri.TryCreate(context.Request.Headers.Referer[0], UriKind.Absolute, out var referer)
            && referer.UserInfo.Length == 0) origin = referer.GetLeftPart(UriPartial.Authority);
        if (origin is null || !options.Value.AllowedOrigins.Contains(origin, StringComparer.Ordinal)) throw new AppException("AUTH_CSRF_INVALID");
    }
    public string Issue(HttpContext context)
    {
        Origin(context);
        var actor = context.User;
        // Bind CSRF to the protected browser cookie, independent of access token expiry/login transitions.
        try { context.User = new ClaimsPrincipal(new ClaimsIdentity()); return antiforgery.GetAndStoreTokens(context).RequestToken!; }
        finally { context.User = actor; }
    }
    public async Task ValidateAsync(HttpContext context)
    {
        Origin(context);
        // Only headers are accepted; never fall back to a form field or query parameter.
        if (context.Request.Headers[BrowserSessionCookies.CsrfHeader].Count != 1
            || context.Request.Headers[BrowserSessionCookies.CsrfHeader].ToString().Length is < 1 or > 4096)
            throw new AppException("AUTH_CSRF_INVALID");
        var actor = context.User;
        try
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity());
            await antiforgery.ValidateRequestAsync(context);
        }
        catch (AntiforgeryValidationException) { throw new AppException("AUTH_CSRF_INVALID"); }
        finally { context.User = actor; }
    }
}
