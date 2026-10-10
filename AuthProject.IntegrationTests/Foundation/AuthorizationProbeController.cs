using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuthProject.IntegrationTests.Foundation;

// Discovered only by test ApplicationPart; exercises inherited default auth and role policy.
[ApiController]
[Route("_tests/p06")]
public sealed class AuthorizationProbeController : ControllerBase
{
    [HttpGet("default")]
    public IActionResult Default() => NoContent();
    [Authorize(Roles = "Admin")]
    [HttpGet("admin")]
    public IActionResult Admin() => NoContent();
}
