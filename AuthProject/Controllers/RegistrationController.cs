using AuthProject.DTOs;
using AuthProject.Security;
using AuthProject.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuthProject.Controllers;

[ApiController]
[AllowAnonymous]
[Route("api/v1/auth")]
public sealed class RegistrationController(IRegistrationService service) : ControllerBase
{
    private static readonly AcceptedEmailResponse AcceptedMessage = new("Nếu yêu cầu hợp lệ, hướng dẫn sẽ được gửi đến email của bạn.");
    [HttpPost("register")]
    [AuthRateLimit(AuthRatePolicy.Register)]
    [ProducesResponseType<AcceptedEmailResponse>(202)]
    public async Task<IActionResult> Register(RegisterRequest request, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        await service.RegisterAsync(request, HttpContext.TraceIdentifier, cancellationToken);
        return Accepted(AcceptedMessage);
    }
    [HttpPost("email/resend")]
    [AuthRateLimit(AuthRatePolicy.Register)]
    [ProducesResponseType<AcceptedEmailResponse>(202)]
    public async Task<IActionResult> Resend(EmailRequest request, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        await service.ResendAsync(request, HttpContext.TraceIdentifier, cancellationToken);
        return Accepted(AcceptedMessage);
    }
    [HttpPost("email/verify")]
    [AuthRateLimit(AuthRatePolicy.Otp)]
    [ProducesResponseType(204)]
    public async Task<IActionResult> Verify(VerifyEmailRequest request, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        await service.VerifyAsync(request, HttpContext.TraceIdentifier, cancellationToken);
        return NoContent();
    }
}
