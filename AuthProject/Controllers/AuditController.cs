using AuthProject.DTOs;
using AuthProject.Security.Authorization;
using AuthProject.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuthProject.Controllers;

[ApiController, Route("api/v1"), ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AuditController(AuditQueryService service) : ControllerBase
{
    [HttpGet("users/me/login-history")]
    public Task<PagedResponse<LoginHistoryResponse>> History([FromQuery] HistoryQuery query, CancellationToken ct) => service.HistoryAsync(AuthenticatedActor.Read(User), query, ct);
    [HttpGet("admin/audit-logs"), Authorize(Policy = AuthPolicies.AdminRead)]
    public Task<PagedResponse<AuditResponse>> Audit([FromQuery] AuditQuery query, CancellationToken ct) => service.AuditAsync(AuthenticatedActor.Read(User), query, ct);
}
