using AuthProject.DTOs;
using AuthProject.Filters;
using AuthProject.Security.Authorization;
using AuthProject.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuthProject.Controllers;

[ApiController, Route("api/v1/admin"), ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Authorize(Policy = AuthPolicies.AdminRead)]
public sealed class AdminController(AdminService service) : ControllerBase
{
    [HttpGet("users")]
    public Task<PagedResponse<AdminUserResponse>> Users([FromQuery] AdminUserQuery query, CancellationToken ct) => service.UsersAsync(AuthenticatedActor.Read(User), query, ct);
    [HttpGet("roles")]
    public Task<RoleResponse[]> Roles(CancellationToken ct) => service.RolesAsync(AuthenticatedActor.Read(User), ct);
    [HttpPut("users/{userId}/roles"), CookieCsrf, Authorize(Policy = AuthPolicies.AdminWrite)]
    [ProducesResponseType(204)]
    public async Task<IActionResult> Roles([FromRoute] UserTargetRequest target, SetUserRolesRequest request, CancellationToken ct)
    { await service.SetRolesAsync(AuthenticatedActor.Read(User), target.UserId, request, HttpContext.TraceIdentifier, ct); return NoContent(); }
    [HttpPatch("users/{userId}/status"), CookieCsrf, Authorize(Policy = AuthPolicies.AdminWrite)]
    [ProducesResponseType(204)]
    public async Task<IActionResult> Status([FromRoute] UserTargetRequest target, ChangeUserStatusRequest request, CancellationToken ct)
    { await service.SetStatusAsync(AuthenticatedActor.Read(User), target.UserId, request, HttpContext.TraceIdentifier, ct); return NoContent(); }
}
