using AuthProject.Configuration;
using Microsoft.Extensions.Options;

namespace AuthProject.Security;

public sealed record RateLimitDemand(string Key, int PermitLimit, TimeSpan Window);
public sealed record RateLimitDecision(bool Allowed, int RetryAfterSeconds);

public sealed class BoundedRateLimitStore(TimeProvider clock, IOptions<BruteForceOptions> options)
{
    private sealed class Counter(DateTimeOffset end) { public DateTimeOffset End = end; public int Used; }
    private readonly Dictionary<string, Counter> counters = new(StringComparer.Ordinal);
    private readonly object sync = new();
    private DateTimeOffset nextSweep;
    private DateTimeOffset earliestExpiry = DateTimeOffset.MaxValue;

    public RateLimitDecision Acquire(IReadOnlyList<RateLimitDemand> demands)
    {
        if (demands.Any(d => d.Key.Length > 128 || d.PermitLimit < 1 || d.Window <= TimeSpan.Zero)
            || demands.Select(d => d.Key).Distinct(StringComparer.Ordinal).Count() != demands.Count)
            throw new ArgumentException("Invalid rate limit demand.", nameof(demands));
        lock (sync)
        {
            var now = clock.GetUtcNow();
            if (now >= nextSweep)
            {
                foreach (var key in counters.Where(x => x.Value.End <= now).Select(x => x.Key).ToArray()) counters.Remove(key);
                earliestExpiry = counters.Count == 0 ? DateTimeOffset.MaxValue : counters.Values.Min(x => x.End);
                nextSweep = now.AddSeconds(1);
            }
            // Reset an accessed partition at the precise boundary, even between sweeps.
            foreach (var d in demands)
                if (counters.TryGetValue(d.Key, out var existing) && existing.End <= now) counters.Remove(d.Key);
            var retry = 0;
            foreach (var d in demands)
                if (counters.TryGetValue(d.Key, out var c) && c.Used >= d.PermitLimit)
                    retry = Math.Max(retry, Seconds(c.End - now));
            if (retry > 0) return new(false, retry);
            var needed = demands.Count(d => !counters.ContainsKey(d.Key));
            if (counters.Count + needed > options.Value.MaxPartitions)
                return new(false, earliestExpiry == DateTimeOffset.MaxValue ? 1 : Seconds(earliestExpiry - now));
            foreach (var d in demands)
            {
                if (!counters.TryGetValue(d.Key, out var c))
                {
                    c = new Counter(now + d.Window);
                    counters.Add(d.Key, c);
                    if (c.End < earliestExpiry) earliestExpiry = c.End;
                }
                c.Used++;
            }
            return new(true, 0);
        }
    }

    private static int Seconds(TimeSpan duration) => Math.Max(1, (int)Math.Ceiling(duration.TotalSeconds));
}
