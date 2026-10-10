using AuthProject.Security;
using AuthProject.DTOs;
using AuthProject.Exceptions;
using AuthProject.Repositories;
using AuthProject.Repositories.Interfaces;
using AuthProject.Security.Authentication;
using AuthProject.Security.Interfaces;
using AuthProject.Services;
using AuthProject.Services.Interfaces;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace AuthProject.Configuration;

public static class LoginServices
{
    public static IServiceCollection AddLogin(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddHttpContextAccessor();
        services.AddOptions<JwtOptions>()
            .Validate<IOptions<PersistenceOptions>>((o, persistence) => !o.Enabled || persistence.Value.Enabled, "Enabled JWT requires persistence.")
            .Validate(o => !o.Enabled || (o.SessionIdleLifetimeSeconds >= o.AccessTokenLifetimeSeconds && o.SessionIdleLifetimeSeconds <= 604800
                && o.SessionAbsoluteLifetimeSeconds >= o.SessionIdleLifetimeSeconds && o.SessionAbsoluteLifetimeSeconds <= 2592000),
                "Enabled JWT requires bounded idle/absolute session lifetimes.");
        services.AddOptions<JwtOptions>().Validate(o => !o.Enabled || (o.Issuer.Length <= 2048
            && Uri.TryCreate(o.Issuer, UriKind.Absolute, out var issuer) && issuer.Scheme == "https"
            && o.Audience.Length <= 256 && o.KeyId.Length <= 64
            && o.KeyId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')),
            "Enabled JWT requires HTTPS issuer and bounded audience/key ID.");
        services.AddSingleton<JwtSigningKey>();
        services.AddSingleton<IAccessTokenIssuer, JwtAccessTokenIssuer>();
        services.AddScoped<ILoginStore, LoginStore>();
        services.AddScoped<ILoginService>(provider => provider.GetRequiredService<IOptions<JwtOptions>>().Value.Enabled
            ? ActivatorUtilities.CreateInstance<LoginService>(provider) : new UnavailableLoginService());
        services.AddScoped<JwtBearerCallbacks>();
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<JwtOptions>, TimeProvider, IServiceProvider>((bearer, configured, clock, provider) =>
            {
                var settings = configured.Value;
                bearer.MapInboundClaims = false;
                bearer.IncludeErrorDetails = false;
                bearer.SaveToken = false;
                bearer.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true, ValidIssuer = settings.Issuer, ValidateAudience = true, ValidAudience = settings.Audience,
                    IgnoreTrailingSlashWhenValidatingAudience = false,
                    ValidateIssuerSigningKey = true, RequireSignedTokens = true, RequireExpirationTime = true, ValidateLifetime = true,
                    ValidAlgorithms = [SecurityAlgorithms.RsaSha256], ValidTypes = ["at+jwt"], ClockSkew = TimeSpan.FromSeconds(settings.ClockSkewSeconds),
                    NameClaimType = "sub", RoleClaimType = "role",
                    IssuerSigningKeyResolver = (_, _, kid, _) => settings.Enabled && kid == settings.KeyId
                        ? [provider.GetRequiredService<JwtSigningKey>().ValidationKey] : [],
                    LifetimeValidator = (nbf, exp, _, _) => AccessTokenRules.Lifetime(nbf, exp, clock.GetUtcNow(), settings.ClockSkewSeconds)
                };
                // Disabled JWT accepts no principal and needs neither a key file nor persistence.
                bearer.EventsType = settings.Enabled ? typeof(JwtBearerCallbacks) : null;
                if (!settings.Enabled) bearer.Events = new JwtBearerEvents
                {
                    OnChallenge = context =>
                    {
                        context.HandleResponse(); context.Response.Headers.WWWAuthenticate = "Bearer";
                        var problems = context.HttpContext.RequestServices.GetRequiredService<ApiProblems>();
                        return problems.WriteAsync(context.HttpContext, problems.Create(context.HttpContext, ApiErrors.Get("AUTH_UNAUTHENTICATED")));
                    }
                };
            });
        services.AddAuthorization();
        services.AddHostedService<JwtKeyStartupCheck>();
        return services;
    }
}

internal sealed class UnavailableLoginService : ILoginService
{
    public Task<SessionGrant> LoginAsync(LoginRequest request, string traceId, string? ip, string? userAgent, CancellationToken cancellationToken)
        => throw new AppException("SERVICE_UNAVAILABLE");
    public Task<MeResponse> GetMeAsync(Guid userId, CancellationToken cancellationToken) => throw new AppException("SERVICE_UNAVAILABLE");
}

internal sealed class JwtKeyStartupCheck(IServiceProvider services, IOptions<JwtOptions> options) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (options.Value.Enabled) _ = services.GetRequiredService<JwtSigningKey>();
        return Task.CompletedTask;
    }
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
