using System.Globalization;
using System.Text.Json;
using AuthProject.Configuration;
using AuthProject.Exceptions;
using AuthProject.Security;
using Microsoft.Extensions.Options;

namespace AuthProject.Middlewares;

public sealed class AuthRateLimitMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IOptions<BruteForceOptions> options,
        BoundedRateLimitStore store, RateLimitKeyFactory keys, ApiProblems problems)
    {
        if (!options.Value.Enabled) { await next(context); return; }
        var ip = keys.ForIp(context.Connection.RemoteIpAddress);
        var decision = new RateLimitDecision(true, 0);
        var policy = context.GetEndpoint()?.Metadata.GetMetadata<AuthRateLimitAttribute>()?.Policy ?? AuthRatePolicy.Api;
        if (policy != AuthRatePolicy.Api)
        {
            var name = policy.ToString();
            var quota = options.Value.Policies[name];
            var demands = new List<RateLimitDemand> { new(name + ":ip:" + ip, quota.IpPermitLimit, TimeSpan.FromSeconds(quota.IpWindowSeconds)) };
            if (quota.AccountPermitLimit > 0 && await Email(context) is { } email)
                demands.Add(new(name + ":account:" + keys.ForAccount(email), quota.AccountPermitLimit, TimeSpan.FromSeconds(quota.WindowSeconds)));
            // Only a principal established by server authentication can supply a session partition.
            if (quota.SessionPermitLimit > 0 && context.GetEndpoint()?.Metadata.GetMetadata<AuthRateLimitAttribute>()?.VerifiedCookieSession != true
                && context.User.Identity?.IsAuthenticated == true
                && Guid.TryParse(context.User.FindFirst("sid")?.Value, out var session))
                demands.Add(new(name + ":session:" + keys.ForSession(session), quota.SessionPermitLimit, TimeSpan.FromSeconds(quota.WindowSeconds)));
            decision = store.Acquire(demands);
        }
        if (!decision.Allowed)
        {
            context.Response.Headers.RetryAfter = decision.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
            await problems.WriteAsync(context, problems.Create(context, ApiErrors.ForStatus(429)), context.RequestAborted);
            return;
        }
        await next(context);
    }

    private static async Task<string?> Email(HttpContext context)
    {
        // The foundation middleware has already bounded and buffered the request body.
        if (!context.Request.Body.CanSeek || !context.Request.HasJsonContentType())
            return null;
        var position = context.Request.Body.Position;
        try
        {
            using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            string? email = null;
            foreach (var property in document.RootElement.EnumerateObject())
                if (property.Name.Equals("email", StringComparison.OrdinalIgnoreCase))
                    email = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
            return email?.Trim() is { Length: > 0 and <= 254 } value ? value : null;
        }
        catch (JsonException) { return null; } // Binding/validation owns malformed-body errors.
        finally { context.Request.Body.Position = position; }
    }
}

public sealed class GlobalRateLimitMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IOptions<BruteForceOptions> options,
        BoundedRateLimitStore store, RateLimitKeyFactory keys, ApiProblems problems)
    {
        if (options.Value.Enabled)
        {
            var quota = options.Value.Policies["Api"];
            var decision = store.Acquire([new("Api:ip:" + keys.ForIp(context.Connection.RemoteIpAddress),
                quota.IpPermitLimit, TimeSpan.FromSeconds(quota.IpWindowSeconds))]);
            if (!decision.Allowed)
            {
                context.Response.Headers.RetryAfter = decision.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
                await problems.WriteAsync(context, problems.Create(context, ApiErrors.ForStatus(429)), context.RequestAborted);
                return;
            }
        }
        await next(context);
    }
}
