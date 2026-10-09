using AuthProject.Configuration;
using AuthProject.Entities;

namespace AuthProject.Security;

public sealed record PasswordAttemptDecision(bool Accepted, AuditReason Reason);

public static class LockoutRules
{
    public static PasswordAttemptDecision Apply(User user, bool passwordMatches, DateTimeOffset now, LockoutOptions options)
    {
        if (user.LockoutEnd > now) return new(false, AuditReason.Locked);
        if (user.LockoutEnd is not null)
        {
            user.LockoutEnd = null;
            user.FailedLoginCount = 0;
            user.FailedLoginWindowStart = null;
        }
        if (user.Status == UserStatus.Disabled) return new(false, AuditReason.Disabled);
        if (user.Status != UserStatus.Active || user.EmailConfirmedAt is null) return new(false, AuditReason.Unverified);
        if (passwordMatches)
        {
            user.FailedLoginCount = 0;
            user.FailedLoginWindowStart = null;
            return new(true, AuditReason.None);
        }
        if (user.FailedLoginWindowStart is null || now >= user.FailedLoginWindowStart.Value.AddSeconds(options.FailureWindowSeconds))
        {
            user.FailedLoginCount = 0;
            user.FailedLoginWindowStart = now;
        }
        user.FailedLoginCount = user.FailedLoginCount >= options.MaxFailedAttempts
            ? options.MaxFailedAttempts : user.FailedLoginCount + 1;
        if (user.FailedLoginCount >= options.MaxFailedAttempts) user.LockoutEnd = now.AddSeconds(options.DurationSeconds);
        return new(false, AuditReason.InvalidCredentials);
    }
}
