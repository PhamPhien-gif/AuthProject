using AuthProject.Entities;
using AuthProject.Security;

namespace UnitTestAuth.Security;

[Trait("Category", "Unit")]
public sealed class RecentAuthenticationTests
{
    [Theory]
    [InlineData(0, true)]
    [InlineData(299, true)]
    [InlineData(300, false)]
    [InlineData(301, false)]
    [InlineData(-1, false)]
    public void RecentPrimary_UsesStrictBoundaryAndRejectsFutureProof(int age, bool expected)
    {
        var now = DateTimeOffset.UtcNow;
        var session = new AuthSession { AuthenticatedAt = now.AddSeconds(-age), LastSeenAt = now, IdleExpiresAt = now.AddDays(7) };
        Assert.Equal(expected, RecentAuthenticationRules.Primary(session, now, 300));
    }
    [Theory]
    [InlineData(0, true)]
    [InlineData(300, false)]
    [InlineData(-1, false)]
    [InlineData(null, false)]
    public void MfaProof_IsIndependentOfFreshPrimaryOrRefresh(int? age, bool expected)
    {
        var now = DateTimeOffset.UtcNow;
        var session = new AuthSession { AuthenticatedAt = now, MfaVerifiedAt = age.HasValue ? now.AddSeconds(-age.Value) : null };
        Assert.Equal(expected, RecentAuthenticationRules.Mfa(session, now, 300));
    }
}
