using AuthProject.Entities;
using AuthProject.Security;

namespace UnitTestAuth.Security;

[Trait("Category", "Unit")]
public sealed class AdminAccessRulesTests
{
    [Theory]
    [InlineData("valid", false, null)] [InlineData("valid", true, null)]
    [InlineData("role", false, "AUTH_FORBIDDEN")]
    [InlineData("enroll", false, "AUTH_MFA_REQUIRED")]
    [InlineData("factor", false, "AUTH_MFA_REQUIRED")]
    [InlineData("old", false, null)] [InlineData("old", true, "AUTH_STEP_UP_REQUIRED")]
    [InlineData("future", true, "AUTH_STEP_UP_REQUIRED")]
    [InlineData("disabled", false, "AUTH_SESSION_INVALID")]
    [InlineData("version", false, "AUTH_SESSION_INVALID")]
    [InlineData("revoked", false, "AUTH_SESSION_INVALID")]
    [InlineData("expired", false, "AUTH_SESSION_INVALID")]
    public void Requires_SQL_role_enrollment_session_and_recent_proof(string state, bool recent, string? expected)
    {
        var now = new DateTimeOffset(2026, 10, 10, 0, 0, 0, TimeSpan.Zero);
        var user = new User { Status = UserStatus.Active, EmailConfirmedAt = now, MfaEmailEnabled = state != "enroll" };
        var session = new AuthSession { UserId = user.Id, AuthVersion = user.AuthVersion, AuthenticatedAt = now,
            MfaVerifiedAt = state == "factor" ? null : now, IdleExpiresAt = now.AddDays(1), AbsoluteExpiresAt = now.AddDays(2) };
        if (state == "old") { session.AuthenticatedAt = now.AddSeconds(-300); session.MfaVerifiedAt = now.AddSeconds(-300); }
        if (state == "future") session.AuthenticatedAt = now.AddSeconds(1);
        if (state == "disabled") user.Status = UserStatus.Disabled;
        if (state == "version") user.AuthVersion++;
        if (state == "revoked") session.RevokedAt = now;
        if (state == "expired") session.IdleExpiresAt = now;
        var actor = new PasswordActor(user.Id, session.Id, session.AuthVersion);
        Assert.Equal(expected, AdminAccessRules.Error(user, session, actor, state != "role", now, 300, recent));
    }
}
