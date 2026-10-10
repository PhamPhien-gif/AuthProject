using AuthProject.Repositories;
using AuthProject.Repositories.Interfaces;
using AuthProject.Security;
using AuthProject.Filters;
using AuthProject.Services;
using Microsoft.Extensions.Options;

namespace AuthProject.Configuration;

public sealed class RefreshOptions
{
    public bool Enabled { get; set; }
    public string[] AllowedOrigins { get; set; } = [];
    public static bool IsOrigin(string origin) => origin.Length <= 2048
        && Uri.TryCreate(origin, UriKind.Absolute, out var uri) && uri.Scheme == "https"
        && uri.UserInfo.Length == 0 && origin == uri.GetLeftPart(UriPartial.Authority);
}

public static class RefreshServices
{
    public const string CorsPolicy = "AuthBrowser";
    public static IServiceCollection AddRefreshSessions(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<RefreshOptions>().Bind(configuration.GetSection("Auth:Refresh"))
            .Validate<IOptions<JwtOptions>>((o, jwt) => !o.Enabled || jwt.Value.Enabled, "Enabled refresh requires JWT.")
            .Validate(o => !o.Enabled || o.AllowedOrigins.Length is > 0 and <= 20 && o.AllowedOrigins.All(RefreshOptions.IsOrigin)
                && o.AllowedOrigins.Distinct(StringComparer.Ordinal).Count() == o.AllowedOrigins.Length,
                "Enabled refresh requires distinct exact HTTPS origins without paths or wildcards.").ValidateOnStart();
        services.AddAntiforgery(o =>
        {
            o.HeaderName = BrowserSessionCookies.CsrfHeader;
            o.Cookie.Name = "__Host-AuthProject.Csrf";
            o.Cookie.Path = "/";
            o.Cookie.HttpOnly = true;
            o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            o.Cookie.SameSite = SameSiteMode.Strict;
        });
        services.AddCors();
        services.AddOptions<Microsoft.AspNetCore.Cors.Infrastructure.CorsOptions>().Configure<IOptions<RefreshOptions>>((cors, options) =>
            cors.AddPolicy(CorsPolicy, policy => policy
                .SetIsOriginAllowed(origin => options.Value.Enabled && options.Value.AllowedOrigins.Contains(origin, StringComparer.Ordinal))
                .WithMethods("GET", "POST", "DELETE").WithHeaders("Content-Type", "Authorization", BrowserSessionCookies.CsrfHeader)
                .WithExposedHeaders("X-Trace-Id", "Retry-After").AllowCredentials()));
        services.AddScoped<BrowserSessionCookies>();
        services.AddScoped<BrowserCsrfGuard>();
        services.AddScoped<CookieCsrfFilter>();
        services.AddScoped<ISessionStore, SessionStore>();
        services.AddScoped(provider => new Lazy<ISessionStore>(() => provider.GetRequiredService<ISessionStore>()));
        services.AddScoped<SessionService>();
        return services;
    }
}
