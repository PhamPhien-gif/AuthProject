using System.Globalization;
using AuthProject.DTOs;
using AuthProject.Filters;
using AuthProject.Security;
using AuthProject.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuthProject.Controllers;

[ApiController, Route("api/v1/auth")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class PasswordRecoveryController(IPasswordRecoveryService service, BrowserSessionCookies cookies) : ControllerBase
{
    [AllowAnonymous, HttpPost("password/forgot"), AuthRateLimit(AuthRatePolicy.Recovery)]
    [ProducesResponseType<AcceptedEmailResponse>(202)]
    public async Task<IActionResult> Forgot(EmailRequest request, CancellationToken ct)
    {
        await service.ForgotAsync(request, HttpContext.TraceIdentifier, ct);
        return Accepted(new AcceptedEmailResponse("Nếu yêu cầu hợp lệ, hướng dẫn sẽ được gửi đến email của bạn."));
    }
    [AllowAnonymous, HttpPost("password/reset"), AuthRateLimit(AuthRatePolicy.Recovery)]
    public async Task<IActionResult> Reset(ResetPasswordRequest request, CancellationToken ct)
    {
        await service.ResetAsync(request, HttpContext.TraceIdentifier, ct);
        cookies.Clear(Response);
        return NoContent();
    }
    [HttpPost("password/change"), CookieCsrf, AuthRateLimit(AuthRatePolicy.Login)]
    public async Task<IActionResult> Change(ChangePasswordRequest request, CancellationToken ct)
    {
        await service.ChangeAsync(Actor(), request, HttpContext.TraceIdentifier, ct);
        cookies.Clear(Response);
        return NoContent();
    }
    [HttpPost("reauthenticate"), CookieCsrf, AuthRateLimit(AuthRatePolicy.Login)]
    public async Task<IActionResult> Reauthenticate(ReauthenticateRequest request, CancellationToken ct)
    {
        await service.ReauthenticateAsync(Actor(), request, HttpContext.TraceIdentifier, ct);
        return NoContent();
    }
    private PasswordActor Actor() => new(Guid.Parse(User.FindFirst("sub")!.Value), Guid.Parse(User.FindFirst("sid")!.Value),
        long.Parse(User.FindFirst("av")!.Value, CultureInfo.InvariantCulture));
}
