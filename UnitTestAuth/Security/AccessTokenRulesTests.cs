using System.Security.Claims;
using AuthProject.Security.Authentication;

namespace UnitTestAuth.Security;

[Trait("Category", "Unit")]
public sealed class AccessTokenRulesTests
{
    [Fact]
    public void Federated_primary_is_valid_but_password_plus_federated_is_rejected()
    {
        var claims = new List<Claim> { new("sub", Guid.NewGuid().ToString()), new("sid", Guid.NewGuid().ToString()),
            new("av", "1"), new("jti", Guid.NewGuid().ToString()), new("iat", "1791504000"), new("amr", "federated") };
        Assert.True(AccessTokenRules.TryRead(new(new ClaimsIdentity(claims)), out _, out _, out _));
        claims.Add(new("amr", "mfa")); Assert.True(AccessTokenRules.TryRead(new(new ClaimsIdentity(claims)), out _, out _, out _));
        claims.Add(new("amr", "pwd")); Assert.False(AccessTokenRules.TryRead(new(new ClaimsIdentity(claims)), out _, out _, out _));
    }
    [Fact]
    public void Claims_accept_password_plus_MFA_but_reject_duplicate_or_untrusted_methods()
    {
        var claims = new List<Claim> { new("sub", Guid.NewGuid().ToString()), new("sid", Guid.NewGuid().ToString()),
            new("av", "1"), new("jti", Guid.NewGuid().ToString()), new("iat", "1791504000"), new("amr", "pwd"), new("amr", "mfa") };
        Assert.True(AccessTokenRules.TryRead(new(new ClaimsIdentity(claims)), out _, out _, out _));
        claims.Add(new("amr", "mfa"));
        Assert.False(AccessTokenRules.TryRead(new(new ClaimsIdentity(claims)), out _, out _, out _));
    }
    [Fact]
    public void Lifetime_RequiresExpiryAndRejectsFutureExpiredOrReversedBounds()
    {
        var now = new DateTimeOffset(2026, 10, 9, 0, 0, 0, TimeSpan.Zero);
        Assert.True(AccessTokenRules.Lifetime(now.UtcDateTime, now.AddMinutes(5).UtcDateTime, now, 0));
        Assert.False(AccessTokenRules.Lifetime(now.UtcDateTime, null, now, 0));
        Assert.False(AccessTokenRules.Lifetime(now.AddSeconds(1).UtcDateTime, now.AddMinutes(5).UtcDateTime, now, 0));
        Assert.False(AccessTokenRules.Lifetime(now.AddMinutes(-5).UtcDateTime, now.UtcDateTime, now, 0));
        Assert.False(AccessTokenRules.Lifetime(now.AddMinutes(6).UtcDateTime, now.AddMinutes(5).UtcDateTime, now, 0));
    }

    [Theory]
    [InlineData("sub")]
    [InlineData("sid")]
    [InlineData("av")]
    [InlineData("jti")]
    [InlineData("iat")]
    [InlineData("amr")]
    public void Claims_RejectMissingRequiredClaim(string missing)
    {
        var claims = new[] { new Claim("sub", Guid.NewGuid().ToString()), new Claim("sid", Guid.NewGuid().ToString()),
            new Claim("av", "1"), new Claim("jti", Guid.NewGuid().ToString()), new Claim("iat", "1791504000"), new Claim("amr", "pwd") };
        Assert.False(AccessTokenRules.TryRead(new ClaimsPrincipal(new ClaimsIdentity(claims.Where(x => x.Type != missing))), out _, out _, out _));
    }

    [Theory]
    [InlineData("sub", "invalid")]
    [InlineData("av", "-1")]
    [InlineData("av", "0")]
    [InlineData("iat", "not-an-integer")]
    [InlineData("amr", "google")]
    public void Claims_RejectInvalidValues(string type, string value)
    {
        var claims = new List<Claim> { new("sub", Guid.NewGuid().ToString()), new("sid", Guid.NewGuid().ToString()),
            new("av", "1"), new("jti", Guid.NewGuid().ToString()), new("iat", "1791504000"), new("amr", "pwd") };
        claims.RemoveAll(x => x.Type == type);
        claims.Add(new Claim(type, value));
        Assert.False(AccessTokenRules.TryRead(new ClaimsPrincipal(new ClaimsIdentity(claims)), out _, out _, out _));
    }
}
