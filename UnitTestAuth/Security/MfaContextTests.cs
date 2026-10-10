using AuthProject.Entities;
using AuthProject.Exceptions;
using AuthProject.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;

namespace UnitTestAuth.Security;

[Trait("Category", "Unit")]
public sealed class MfaContextTests
{
    [Fact]
    public void Cookie_roundtrip_preserves_binding_and_rejects_other_challenge_and_key()
    {
        var context = new MfaContext(new EphemeralDataProtectionProvider());
        var ticket = new MfaTicket(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), ChallengePurpose.StepUp, 7, Guid.NewGuid());
        var response = new DefaultHttpContext().Response;
        context.Write(response, new(new(ticket.ChallengeId, "StepUp", DateTimeOffset.UtcNow.AddMinutes(5), null), ticket));
        var cookie = response.Headers.SetCookie.ToString().Split(';')[0];
        var request = new DefaultHttpContext().Request; request.Headers.Cookie = cookie;
        Assert.Equal(ticket, context.Read(request, ticket.ChallengeId));
        Assert.Throws<AppException>(() => context.Read(request, Guid.NewGuid()));
        Assert.Throws<AppException>(() => new MfaContext(new EphemeralDataProtectionProvider()).Read(request, ticket.ChallengeId));
        Assert.Contains("secure", response.Headers.SetCookie.ToString()); Assert.Contains("httponly", response.Headers.SetCookie.ToString());
    }
    [Theory]
    [InlineData(null)] [InlineData("tampered")] [InlineData("%bad")]
    public void Missing_or_tampered_cookie_is_public_challenge_error(string? value)
    {
        var request = new DefaultHttpContext().Request;
        if (value is not null) request.Headers.Cookie = MfaContext.CookieName + "=" + value;
        Assert.Equal("AUTH_CHALLENGE_INVALID", Assert.Throws<AppException>(() =>
            new MfaContext(new EphemeralDataProtectionProvider()).Read(request, Guid.NewGuid())).Error.Code);
    }
}
