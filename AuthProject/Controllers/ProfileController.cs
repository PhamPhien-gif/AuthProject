using AuthProject.DTOs;
using AuthProject.Filters;
using AuthProject.Security.Authorization;
using AuthProject.Services;
using Microsoft.AspNetCore.Mvc;

namespace AuthProject.Controllers;

[ApiController, Route("api/v1/users/me"), ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class ProfileController(AdminService service) : ControllerBase
{
    [HttpPatch, CookieCsrf, ProducesResponseType(204)]
    public async Task<IActionResult> Update(UpdateProfileRequest request, CancellationToken ct)
    { await service.ProfileAsync(AuthenticatedActor.Read(User), request, HttpContext.TraceIdentifier, ct); return NoContent(); }
}
