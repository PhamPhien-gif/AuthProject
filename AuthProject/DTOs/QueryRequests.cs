using System.Text.Json.Serialization;

namespace AuthProject.DTOs;

public class PagingRequest
{
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 20;
}

public sealed class HistoryQuery : PagingRequest
{
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
    public string Sort { get; init; } = "occurredAtDesc";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SetUserRolesRequest(string[]? RoleNames);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ChangeUserStatusRequest(string? Status);
