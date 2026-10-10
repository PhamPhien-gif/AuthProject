using AuthProject.DTOs;
using AuthProject.Security;
using AuthProject.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuthProject.Controllers;

[ApiController]
[Route("api/v1/auth")]
public sealed class LoginController(ILoginService service) : ControllerBase
{
    [AllowAnonymous]
    [HttpPost("login")]
    [AuthRateLimit(AuthRatePolicy.Login)]
    [ProducesResponseType<AuthResult>(200)]
    public async Task<AuthResult> Login(LoginRequest request, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        return await service.LoginAsync(request, HttpContext.TraceIdentifier, HttpContext.Connection.RemoteIpAddress?.ToString(),
            Request.Headers.UserAgent.ToString(), cancellationToken);
    }
    [HttpGet("me")]
    [ProducesResponseType<MeResponse>(200)]
    public async Task<MeResponse> Me(CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        return await service.GetMeAsync(Guid.Parse(User.FindFirst("sub")!.Value), cancellationToken);
    }
}
