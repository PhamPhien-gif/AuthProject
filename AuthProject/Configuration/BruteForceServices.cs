using System.Net;
using AuthProject.Repositories;
using AuthProject.Security;
using AuthProject.Security.Interfaces;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;

namespace AuthProject.Configuration;

public static class BruteForceServices
{
    public static IServiceCollection AddBruteForceProtection(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<BruteForceOptions>().Bind(configuration.GetSection("BruteForce:RateLimit"))
            .Validate(o => o.MaxPartitions is >= 10 and <= 100000
                && Enum.GetNames<AuthRatePolicy>().All(name => o.Policies.TryGetValue(name, out var q)
                    && q.IpPermitLimit is >= 1 and <= 10000 && q.AccountPermitLimit is >= 0 and <= 10000
                    && q.SessionPermitLimit is >= 0 and <= 10000
                    && q.WindowSeconds is >= 1 and <= 86400 && q.IpWindowSeconds is >= 1 and <= 86400),
                "Rate limits require all named policies, bounded partitions and valid quotas/windows.").ValidateOnStart();
        services.AddOptions<LockoutOptions>().Bind(configuration.GetSection("BruteForce:Lockout"))
            .Validate(o => o.MaxFailedAttempts is >= 1 and <= 20 && o.FailureWindowSeconds is >= 60 and <= 86400
                && o.DurationSeconds is >= 60 and <= 86400, "Lockout requires valid threshold, failure window and finite cooldown.").ValidateOnStart();
        services.AddOptions<TrustedProxyOptions>().Bind(configuration.GetSection("TrustedProxy"))
            .Validate(o => !o.Enabled || (o.ForwardLimit is >= 1 and <= 5
                && o.KnownProxies.Length + o.KnownNetworks.Length > 0
                && o.KnownProxies.All(value => IPAddress.TryParse(value, out _))
                && o.KnownNetworks.All(value => System.Net.IPNetwork.TryParse(value, out var network) && network.PrefixLength > 0)),
                "Enabled trusted proxy requires explicit IPs/networks and a bounded forward limit; wildcard networks are forbidden.").ValidateOnStart();
        services.AddOptions<ForwardedHeadersOptions>().PostConfigure<IOptions<TrustedProxyOptions>>((o, validated) =>
        {
            // Override framework defaults/auto-forwarding too: no implicit loopback or trust-all list.
            var settings = validated.Value;
            o.KnownProxies.Clear();
            o.KnownIPNetworks.Clear();
            o.ForwardedHeaders = ForwardedHeaders.None;
            if (!settings.Enabled) return;
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            o.ForwardLimit = settings.ForwardLimit;
            o.RequireHeaderSymmetry = true;
            foreach (var ip in settings.KnownProxies) o.KnownProxies.Add(IPAddress.Parse(ip));
            foreach (var network in settings.KnownNetworks) o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
        });
        services.AddSingleton<BoundedRateLimitStore>();
        services.AddSingleton<RateLimitKeyFactory>();
        services.AddSingleton<DummyPasswordHash>();
        services.AddScoped<IPasswordAttemptStore, PasswordAttemptStore>();
        services.AddScoped<IPasswordCredentialGate, PasswordCredentialGate>();
        return services;
    }
}
