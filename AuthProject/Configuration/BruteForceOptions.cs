namespace AuthProject.Configuration;

public sealed class RateQuota
{
    public int IpPermitLimit { get; set; }
    public int AccountPermitLimit { get; set; }
    public int SessionPermitLimit { get; set; }
    public int WindowSeconds { get; set; } = 60;
    public int IpWindowSeconds { get; set; } = 60;
}

public sealed class BruteForceOptions
{
    public bool Enabled { get; set; } = true;
    public int MaxPartitions { get; set; } = 10000;
    public Dictionary<string, RateQuota> Policies { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Api"] = new() { IpPermitLimit = 200 },
        ["Login"] = new() { IpPermitLimit = 10, AccountPermitLimit = 30, WindowSeconds = 900 },
        ["Register"] = new() { IpPermitLimit = 5, AccountPermitLimit = 5, WindowSeconds = 900, IpWindowSeconds = 900 },
        ["Otp"] = new() { IpPermitLimit = 10 },
        ["Refresh"] = new() { IpPermitLimit = 60, SessionPermitLimit = 30 },
        ["Recovery"] = new() { IpPermitLimit = 5, AccountPermitLimit = 5, WindowSeconds = 900, IpWindowSeconds = 900 },
        ["Google"] = new() { IpPermitLimit = 10 }
    };
}

public sealed class LockoutOptions
{
    public int MaxFailedAttempts { get; set; } = 5;
    public int FailureWindowSeconds { get; set; } = 900;
    public int DurationSeconds { get; set; } = 900;
}

public sealed class TrustedProxyOptions
{
    public bool Enabled { get; set; }
    public int ForwardLimit { get; set; } = 1;
    public string[] KnownProxies { get; set; } = [];
    public string[] KnownNetworks { get; set; } = [];
}
