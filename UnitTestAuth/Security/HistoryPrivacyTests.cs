using AuthProject.Security;

namespace UnitTestAuth.Security;

[Trait("Category", "Unit")]
public sealed class HistoryPrivacyTests
{
    [Theory]
    [InlineData("192.0.2.123", "192.0.2.0/24")]
    [InlineData("::ffff:192.0.2.123", "192.0.2.0/24")]
    [InlineData("2001:db8:1234:5678::1", "2001:db8:1234::/48")]
    [InlineData("invalid-secret", null)] [InlineData(null, null)]
    public void Masks_addresses_without_echoing_invalid_input(string? input, string? expected) => Assert.Equal(expected, HistoryPrivacy.MaskIp(input));
}
