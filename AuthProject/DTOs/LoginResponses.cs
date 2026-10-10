namespace AuthProject.DTOs;

public sealed record AuthUserResponse(Guid Id, string Email, string DisplayName, string[] Roles);
public sealed record AuthResult(string AccessToken, string TokenType, int ExpiresIn, AuthUserResponse User);
public sealed record MeResponse(Guid Id, string Email, string DisplayName, string[] Roles, bool EmailVerified, bool MfaEnabled, DateTimeOffset CreatedAt);
