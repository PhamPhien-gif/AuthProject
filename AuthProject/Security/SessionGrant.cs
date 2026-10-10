using AuthProject.DTOs;

namespace AuthProject.Security;

// Internal handoff to the cookie writer; controllers return Response, never this object.
// A class deliberately avoids a generated record ToString containing the secret.
public sealed class SessionGrant(AuthResult response, string refreshToken, DateTimeOffset expiresAt)
{
    public AuthResult Response { get; } = response;
    public string RefreshToken { get; } = refreshToken;
    public DateTimeOffset ExpiresAt { get; } = expiresAt;
}
