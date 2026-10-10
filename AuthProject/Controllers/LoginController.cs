using AuthProject.DTOs;
using AuthProject.Security;
using AuthProject.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AuthProject.Filters;
using AuthProject.Services;

namespace AuthProject.Controllers;

[ApiController]
[Route("api/v1/auth")]
public sealed class LoginController(ILoginService service, BrowserSessionCookies cookies, MfaService mfa, MfaContext context) : ControllerBase
{
    [AllowAnonymous]
    [HttpPost("login")]
    [AuthRateLimit(AuthRatePolicy.Login)]
    [CookieCsrf]
    [ProducesResponseType<AuthResult>(200)]
    [ProducesResponseType<MfaChallengeResponse>(202)]
    public async Task<IActionResult> Login(LoginRequest request, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        SessionGrant grant;
        if (mfa.Enabled)
        {
            var result = await mfa.LoginAsync(request, HttpContext.TraceIdentifier, HttpContext.Connection.RemoteIpAddress?.ToString(),
                Request.Headers.UserAgent.ToString(), cancellationToken);
            if (result.Challenge is not null)
            {
                context.Write(Response, result.Challenge);
                return Accepted(result.Challenge.Response);
            }
            grant = result.Session!;
        }
        else grant = await service.LoginAsync(request, HttpContext.TraceIdentifier, HttpContext.Connection.RemoteIpAddress?.ToString(),
            Request.Headers.UserAgent.ToString(), cancellationToken);
        cookies.Write(Response, grant);
        if (mfa.Enabled) context.Clear(Response);
        return Ok(grant.Response);
    }
    [HttpGet("me")]
    [ProducesResponseType<MeResponse>(200)]
    public async Task<MeResponse> Me(CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        return await service.GetMeAsync(Guid.Parse(User.FindFirst("sub")!.Value), cancellationToken);
    }
}
