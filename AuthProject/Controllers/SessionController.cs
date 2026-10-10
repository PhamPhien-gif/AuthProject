using System.Globalization;
using AuthProject.DTOs;
using AuthProject.Exceptions;
using AuthProject.Filters;
using AuthProject.Security;
using AuthProject.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuthProject.Controllers;

[ApiController]
[Route("api/v1/auth")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class SessionController(SessionService service, BrowserSessionCookies cookies, BrowserCsrfGuard csrf, ApiProblems problems) : ControllerBase
{
    [AllowAnonymous, HttpGet("csrf")]
    public CsrfResponse Csrf() { service.RequireEnabled(); return new(csrf.Issue(HttpContext)); }

    [AllowAnonymous, HttpPost("refresh"), CookieCsrf]
    [AuthRateLimit(AuthRatePolicy.Refresh, VerifiedCookieSession = true)]
    public async Task<ActionResult<AuthResult>> Refresh(CancellationToken ct)
    {
        var attempt = await service.RefreshAsync(Request.Cookies[BrowserSessionCookies.Name], HttpContext.TraceIdentifier, ct);
        if (attempt.RetryAfterSeconds is { } seconds)
        {
            Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
            return new ApiProblemResult(problems.Create(HttpContext, ApiErrors.Get("RATE_LIMITED")));
        }
        if (attempt.Grant is null)
        {
            cookies.Clear(Response);
            return new ApiProblemResult(problems.Create(HttpContext, ApiErrors.Get("AUTH_REFRESH_INVALID")));
        }
        cookies.Write(Response, attempt.Grant);
        return attempt.Grant.Response;
    }

    [AllowAnonymous, HttpPost("logout"), CookieCsrf]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        await service.LogoutAsync(Request.Cookies[BrowserSessionCookies.Name], HttpContext.TraceIdentifier, ct);
        cookies.Clear(Response);
        return NoContent();
    }
    [HttpPost("logout-all"), CookieCsrf]
    public async Task<IActionResult> LogoutAll(CancellationToken ct)
    {
        await service.LogoutAllAsync(Actor(), HttpContext.TraceIdentifier, ct);
        cookies.Clear(Response);
        return NoContent();
    }
    [HttpGet("sessions")]
    public Task<PagedResponse<SessionResponse>> Sessions([FromQuery] PagingRequest paging, CancellationToken ct) => service.ListAsync(Actor(), Current(), paging, ct);

    [HttpDelete("sessions/{sessionId}"), CookieCsrf]
    public async Task<IActionResult> Revoke([FromRoute] RevokeSessionRequest request, CancellationToken ct)
    {
        var sessionId = request.SessionId;
        await service.RevokeAsync(Actor(), sessionId, HttpContext.TraceIdentifier, ct);
        if (sessionId == Current()) cookies.Clear(Response);
        return NoContent();
    }
    private Guid Actor() => Guid.Parse(User.FindFirst("sub")!.Value);
    private Guid Current() => Guid.Parse(User.FindFirst("sid")!.Value);
}
