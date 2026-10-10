using System.Globalization;
using System.Data.Common;
using System.Security.Claims;
using AuthProject.Configuration;
using AuthProject.Exceptions;
using AuthProject.Repositories.Interfaces;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;

namespace AuthProject.Security.Authentication;

public sealed class JwtBearerCallbacks(ILoginStore store, ApiProblems problems, TimeProvider clock, IOptions<JwtOptions> options) : JwtBearerEvents
{
    public override Task MessageReceived(MessageReceivedContext context)
    {
        if (context.Request.Headers.Authorization.ToString().Length > 8192) context.Fail("Access token exceeds the allowed size.");
        return Task.CompletedTask;
    }
    public override async Task TokenValidated(TokenValidatedContext context)
    {
        if (context.Principal is null || !AccessTokenRules.TryRead(context.Principal, out var user, out var session, out var version)
            || long.Parse(context.Principal.FindFirst("iat")!.Value, CultureInfo.InvariantCulture) > clock.GetUtcNow().ToUnixTimeSeconds() + options.Value.ClockSkewSeconds)
        { context.Fail("Invalid access token claims."); return; }
        string[]? roles;
        try { roles = await store.ValidateSessionAsync(user, session, version, context.HttpContext.RequestAborted); }
        catch (DbException) { throw new AppException("SERVICE_UNAVAILABLE"); }
        if (roles is null) { context.Fail("Invalid authentication session."); return; }
        var identity = (ClaimsIdentity)context.Principal.Identity!;
        foreach (var claim in identity.FindAll("role").ToArray()) identity.RemoveClaim(claim);
        foreach (var role in roles) identity.AddClaim(new Claim("role", role));
    }
    public override Task Challenge(JwtBearerChallengeContext context)
    {
        context.HandleResponse();
        context.Response.Headers.WWWAuthenticate = "Bearer";
        return problems.WriteAsync(context.HttpContext, problems.Create(context.HttpContext, ApiErrors.Get("AUTH_UNAUTHENTICATED")), context.HttpContext.RequestAborted);
    }
    public override Task Forbidden(ForbiddenContext context) => problems.WriteAsync(context.HttpContext,
        problems.Create(context.HttpContext, ApiErrors.Get("AUTH_FORBIDDEN")), context.HttpContext.RequestAborted);
}
