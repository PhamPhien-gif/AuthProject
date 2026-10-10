using System.Net;

namespace AuthProject.Security;

public static class HistoryPrivacy
{
    public static string? MaskIp(string? input)
    {
        if (input is null) return null;
        // Accept already masked values produced by this method, but never arbitrary strings.
        var address = input.EndsWith("/24", StringComparison.Ordinal) || input.EndsWith("/48", StringComparison.Ordinal) ? input[..^3] : input;
        if (!IPAddress.TryParse(address, out var ip)) return null;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        var bytes = ip.GetAddressBytes(); var kept = bytes.Length == 4 ? 3 : 6;
        Array.Clear(bytes, kept, bytes.Length - kept);
        return new IPAddress(bytes) + (bytes.Length == 4 ? "/24" : "/48");
    }
}
