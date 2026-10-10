using System.Globalization;
using AuthProject.DTOs;
using AuthProject.Entities;
using AuthProject.Filters;
using AuthProject.Security;
using AuthProject.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuthProject.Controllers;

[ApiController, Route("api/v1/auth/mfa")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[AuthRateLimit(AuthRatePolicy.Otp)]
public sealed class MfaController(MfaService service, MfaContext context, BrowserSessionCookies sessions) : ControllerBase
{
    [CookieCsrf, HttpPost("email/enroll"), ProducesResponseType<MfaChallengeResponse>(202)]
    public async Task<IActionResult> Enroll(CancellationToken ct)
    {
        var grant = await service.EnrollAsync(Actor()!, HttpContext.TraceIdentifier, ct);
        context.Write(Response, grant);
        return Accepted(grant.Response);
    }
    [AuthRateLimit(AuthRatePolicy.Login), CookieCsrf, HttpPost("email/step-up"), ProducesResponseType<MfaChallengeResponse>(202)]
    public async Task<IActionResult> StepUp(ReauthenticateRequest request, CancellationToken ct)
    {
        var grant = await service.StepUpAsync(Actor()!, request, HttpContext.TraceIdentifier, ct);
        context.Write(Response, grant);
        return Accepted(grant.Response);
    }
    [CookieCsrf, AllowAnonymous, HttpPost("email/verify")]
    [ProducesResponseType<AuthResult>(200), ProducesResponseType(204)]
    public Task<IActionResult> Verify(VerifyOtpRequest request, CancellationToken ct) => Complete(request.ChallengeId, request.Code!, false, ct);
    [CookieCsrf, AllowAnonymous, HttpPost("recovery/verify")]
    [ProducesResponseType<AuthResult>(200), ProducesResponseType(204)]
    public Task<IActionResult> Recovery(RecoveryCodeRequest request, CancellationToken ct) => Complete(request.ChallengeId, request.Code!, true, ct);
    [CookieCsrf, HttpPost("email/enable"), ProducesResponseType<RecoveryCodesResponse>(200)]
    public async Task<IActionResult> Enable(VerifyOtpRequest request, CancellationToken ct)
    {
        var ticket = Ticket(request.ChallengeId);
        if (ticket.Purpose != ChallengePurpose.EnrollMfa) throw new AuthProject.Exceptions.AppException("AUTH_CHALLENGE_INVALID");
        var result = await service.VerifyAsync(ticket, Actor(), request.Code!, false, HttpContext.TraceIdentifier, ct);
        context.Clear(Response); sessions.Clear(Response);
        return Ok(result.Recovery);
    }
    [CookieCsrf, AllowAnonymous, HttpPost("email/resend"), ProducesResponseType<MfaChallengeResponse>(202)]
    public async Task<IActionResult> Resend(ChallengeRequest request, CancellationToken ct)
    {
        var grant = await service.ResendAsync(Ticket(request.ChallengeId), Actor(), HttpContext.TraceIdentifier, ct);
        context.Write(Response, grant);
        return Accepted(grant.Response);
    }
    [CookieCsrf, HttpPost("email/disable"), ProducesResponseType(204)]
    public async Task<IActionResult> Disable(CancellationToken ct)
    {
        await service.ChangeAsync(Actor()!, true, HttpContext.TraceIdentifier, ct);
        context.Clear(Response); sessions.Clear(Response);
        return NoContent();
    }
    [CookieCsrf, HttpPost("recovery/regenerate"), ProducesResponseType<RecoveryCodesResponse>(200)]
    public async Task<IActionResult> Regenerate(CancellationToken ct)
    {
        var codes = await service.ChangeAsync(Actor()!, false, HttpContext.TraceIdentifier, ct);
        context.Clear(Response); sessions.Clear(Response);
        return Ok(codes);
    }
    private async Task<IActionResult> Complete(Guid challengeId, string code, bool recovery, CancellationToken ct)
    {
        var ticket = Ticket(challengeId);
        if (ticket.Purpose == ChallengePurpose.EnrollMfa) throw new AuthProject.Exceptions.AppException("AUTH_CHALLENGE_INVALID");
        var result = await service.VerifyAsync(ticket, Actor(), code, recovery, HttpContext.TraceIdentifier, ct);
        context.Clear(Response);
        if (result.Session is null) return NoContent();
        sessions.Write(Response, result.Session);
        return Ok(result.Session.Response);
    }
    private PasswordActor? Actor() => User.Identity?.IsAuthenticated == true
        ? new(Guid.Parse(User.FindFirst("sub")!.Value), Guid.Parse(User.FindFirst("sid")!.Value),
            long.Parse(User.FindFirst("av")!.Value, CultureInfo.InvariantCulture)) : null;
    private MfaTicket Ticket(Guid id)
    {
        if (!service.Enabled) throw new AuthProject.Exceptions.AppException("SERVICE_UNAVAILABLE");
        return context.Read(Request, id);
    }
}
