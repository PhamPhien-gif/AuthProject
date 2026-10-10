using System.Security.Claims;
using AuthProject.Data.Configurations;
using AuthProject.Exceptions;
using AuthProject.Security.Authentication;
using AuthProject.Services;
using Microsoft.AspNetCore.Authorization;

namespace AuthProject.Security.Authorization;

public static class AuthPolicies
{
    public const string AdminRead = "Admin.Read";
    public const string AdminWrite = "Admin.Write";
}
public static class AuthenticatedActor
{
    public static PasswordActor Read(ClaimsPrincipal principal) => AccessTokenRules.TryRead(principal, out var user, out var session, out var version)
        ? new(user, session, version) : throw new AppException("AUTH_SESSION_INVALID");
}
public sealed record AdminRequirement(bool Recent) : IAuthorizationRequirement;
public sealed class AdminAuthorizationHandler(AdminService service, IHttpContextAccessor http) : AuthorizationHandler<AdminRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, AdminRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated != true || !context.User.IsInRole(SystemRoles.Admin)) { context.Fail(); return; }
        await service.AuthorizeAsync(AuthenticatedActor.Read(context.User), requirement.Recent, http.HttpContext?.RequestAborted ?? default);
        context.Succeed(requirement);
    }
}
