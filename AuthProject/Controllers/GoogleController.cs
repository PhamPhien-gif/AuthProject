using System.Globalization;
using AuthProject.DTOs;
using AuthProject.Filters;
using AuthProject.Security;
using AuthProject.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuthProject.Controllers;

[ApiController, Route("api/v1/auth/google"), ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[AuthRateLimit(AuthRatePolicy.Google)]
public sealed class GoogleController(GoogleService service, GoogleBrowserContext browser, MfaContext mfa,
    BrowserSessionCookies cookies, TimeProvider clock) : ControllerBase
{
    [AllowAnonymous, HttpGet("start")]
    public IActionResult Start() { service.RequireEnabled(); return Begin(GoogleFlow.Login, null); }
    [HttpPost("link"), CookieCsrf]
    [ProducesResponseType<GoogleAuthorizationResponse>(200)]
    public async Task<IActionResult> Link(CancellationToken ct)
    { var actor = Actor()!; await service.AuthorizeActorAsync(actor, true, ct); return Begin(GoogleFlow.Link, actor); }
    [HttpPost("reauthenticate"), CookieCsrf]
    [ProducesResponseType<GoogleAuthorizationResponse>(200)]
    public async Task<IActionResult> Reauthenticate(CancellationToken ct)
    { var actor = Actor()!; await service.AuthorizeActorAsync(actor, false, ct); return Begin(GoogleFlow.Reauthenticate, actor); }
    [HttpDelete("link"), CookieCsrf]
    public async Task<IActionResult> Unlink(CancellationToken ct)
    { await service.UnlinkAsync(Actor()!, HttpContext.TraceIdentifier, ct); browser.Clear(Response); cookies.Clear(Response); mfa.Clear(Response); return NoContent(); }
    [AllowAnonymous, HttpPost("complete"), CookieCsrf]
    [ProducesResponseType<AuthResult>(200), ProducesResponseType<MfaChallengeResponse>(202), ProducesResponseType(204)]
    public async Task<IActionResult> Complete(GoogleCompleteRequest request, CancellationToken ct)
    {
        service.RequireEnabled();
        var binding = browser.Read(Request, true);
        var result = await service.CompleteAsync(binding, request.Ticket!, Actor(), HttpContext.TraceIdentifier, ct);
        browser.Clear(Response);
        if (result.Challenge is not null) { mfa.Write(Response, result.Challenge); return Accepted(result.Challenge.Response); }
        if (result.Session is not null) { cookies.Write(Response, result.Session); return Ok(result.Session.Response); }
        if (binding.Flow == GoogleFlow.Link) cookies.Clear(Response);
        return NoContent();
    }
    private IActionResult Begin(GoogleFlow flow, PasswordActor? actor)
    {
        var binding = new GoogleBinding(Guid.NewGuid(), flow, clock.GetUtcNow(), actor);
        browser.Start(Response, binding);
        var properties = new AuthenticationProperties(); properties.Items["google-context"] = binding.ContextId.ToString("N");
        properties.Items["google-flow"] = flow.ToString();
        return flow == GoogleFlow.Login ? Challenge(properties, Configuration.GoogleServices.Scheme)
            : new AuthorizationUrlResult(properties);
    }
    // Finish the standard challenge (including protected state) before exposing its URL.
    // Browser fetch cannot follow a Google redirect while carrying API bearer/CSRF headers.
    private sealed class AuthorizationUrlResult(AuthenticationProperties properties) : IActionResult
    {
        public async Task ExecuteResultAsync(ActionContext context)
        {
            var http = context.HttpContext;
            await http.ChallengeAsync(Configuration.GoogleServices.Scheme, properties);
            var url = http.Response.Headers.Location.ToString();
            http.Response.Headers.Remove("Location");
            await new OkObjectResult(new GoogleAuthorizationResponse(url)).ExecuteResultAsync(context);
        }
    }
    private PasswordActor? Actor() => User.Identity?.IsAuthenticated == true ? new(Guid.Parse(User.FindFirst("sub")!.Value),
        Guid.Parse(User.FindFirst("sid")!.Value), long.Parse(User.FindFirst("av")!.Value, CultureInfo.InvariantCulture)) : null;
}
