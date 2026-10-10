using System.Net;
using AuthProject.DTOs;
using AuthProject.Security;
using AuthProject.Security.Interfaces;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;

namespace AuthProject.IntegrationTests.Foundation;

[ApiController]
[Microsoft.AspNetCore.Authorization.AllowAnonymous]
[Route("_tests/p04")]
public sealed class BruteForceProbeController : ControllerBase
{
    [HttpPost("login")]
    [AuthRateLimit(AuthRatePolicy.Login)]
    public IActionResult Login(LoginRequest request) => NoContent();

    [HttpPost("register")]
    [AuthRateLimit(AuthRatePolicy.Register)]
    public IActionResult Register() => NoContent();
    [HttpPost("otp")]
    [AuthRateLimit(AuthRatePolicy.Otp)]
    public IActionResult Otp() => NoContent();
    [HttpPost("refresh")]
    [AuthRateLimit(AuthRatePolicy.Refresh)]
    public IActionResult Refresh() => NoContent();
    [HttpPost("recovery")]
    [AuthRateLimit(AuthRatePolicy.Recovery)]
    public IActionResult Recovery() => NoContent();
    [HttpPost("google")]
    [AuthRateLimit(AuthRatePolicy.Google)]
    public IActionResult Google() => NoContent();

    [HttpPost("credential")]
    public async Task<IActionResult> Credential(LoginRequest request, [FromServices] IPasswordCredentialGate gate)
    {
        await gate.CheckAsync(request.Email!, request.Password!, HttpContext.TraceIdentifier, HttpContext.RequestAborted);
        return NoContent();
    }

    [HttpGet("ip")]
    public IActionResult Ip() => Ok(new { ip = HttpContext.Connection.RemoteIpAddress?.ToString(), scheme = Request.Scheme });
}

// Test-only socket simulation: HTTP headers cannot set RemoteIpAddress in the real application.
internal sealed class TestSocketFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use((context, onward) =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse(context.Request.Headers["X-Test-Socket-Ip"].FirstOrDefault() ?? "192.0.2.1");
            if (Guid.TryParse(context.Request.Headers["X-Test-Validated-Session"].FirstOrDefault(), out var session))
                context.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                    [new System.Security.Claims.Claim("sid", session.ToString())], "TestOnly"));
            return onward(context);
        });
        next(app);
    };
}
