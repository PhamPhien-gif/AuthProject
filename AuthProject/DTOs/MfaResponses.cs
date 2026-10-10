namespace AuthProject.DTOs;

public sealed record MfaChallengeResponse(Guid ChallengeId, string Purpose, DateTimeOffset ExpiresAt, DateTimeOffset? NextSendAt);
public sealed record RecoveryCodesResponse(string[] RecoveryCodes);
