using System.Text.Json.Serialization;

namespace AuthProject.DTOs;

public sealed class AdminUserQuery : PagingRequest
{
    public string? Status { get; init; }
    public string? Search { get; init; }
}
public sealed class UserTargetRequest { public Guid UserId { get; init; } }
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateProfileRequest(string? DisplayName);
public sealed record AdminUserResponse(Guid Id, string Email, string DisplayName, string Status, bool MfaEnabled,
    DateTimeOffset CreatedAt, string[] Roles);
public sealed record RoleResponse(Guid Id, string Name, bool IsSystem);
