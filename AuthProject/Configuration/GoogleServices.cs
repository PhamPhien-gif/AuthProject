using AuthProject.Exceptions;
using AuthProject.Repositories;
using AuthProject.Security;
using AuthProject.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace AuthProject.Configuration;

public static class GoogleServices
{
    public const string Scheme = "Google.Oidc";
    public static IServiceCollection AddGoogleLogin(this IServiceCollection services)
    {
        // Framework OIDC logs can include provider-controlled error_description / token failures.
        services.AddLogging(logging => logging.AddFilter("Microsoft.AspNetCore.Authentication.OpenIdConnect", LogLevel.None));
        services.AddOptions<GoogleOptions>()
            .Validate<IOptions<JwtOptions>, IOptions<RefreshOptions>>((o, jwt, refresh) => !o.Enabled || jwt.Value.Enabled && refresh.Value.Enabled,
                "Google login requires JWT and browser refresh/CSRF protection.")
            .Validate<IOptions<RefreshOptions>>((o, refresh) => !o.Enabled || SafeUrl(o.CallbackUri) && SafeUrl(o.CompletionUrl) && SafeUrl(o.ErrorUrl)
                && o.CallbackPath == "/signin-google" && new Uri(o.CallbackUri).AbsolutePath == o.CallbackPath
                && refresh.Value.AllowedOrigins.Contains(new Uri(o.CompletionUrl).GetLeftPart(UriPartial.Authority), StringComparer.Ordinal)
                && refresh.Value.AllowedOrigins.Contains(new Uri(o.ErrorUrl).GetLeftPart(UriPartial.Authority), StringComparer.Ordinal)
                && o.ClientId.Length <= 256 && o.ClientSecret.Length <= 512,
                "Google login requires fixed HTTPS callback/completion/error URLs and allowlisted client origins.");
        services.AddSingleton<GoogleBrowserContext>(); services.AddScoped<GoogleStore>();
        services.AddScoped(p => new Lazy<GoogleStore>(() => p.GetRequiredService<GoogleStore>())); services.AddScoped<GoogleService>();
        services.AddAuthentication().AddCookie("Google.External", o =>
        {
            o.Cookie.Name = "__Host-AuthProject.External"; o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            o.Cookie.SameSite = SameSiteMode.Strict; o.ExpireTimeSpan = TimeSpan.FromMinutes(1);
            o.Events.OnSigningIn = _ => throw new InvalidOperationException("Provider principals cannot become API sessions.");
        }).AddOpenIdConnect(Scheme, _ => { });
        services.AddOptions<OpenIdConnectOptions>(Scheme).Configure<IOptions<GoogleOptions>, TimeProvider>((o, configured, clock) =>
        {
            var settings = configured.Value;
            o.Authority = "https://accounts.google.com"; o.RequireHttpsMetadata = true; o.SignInScheme = "Google.External";
            o.ClientId = settings.Enabled ? settings.ClientId : "disabled"; o.ClientSecret = settings.Enabled ? settings.ClientSecret : "disabled";
            o.CallbackPath = settings.Enabled ? settings.CallbackPath : "/_google-disabled";
            o.ResponseType = OpenIdConnectResponseType.Code; o.ResponseMode = OpenIdConnectResponseMode.Query; o.UsePkce = true;
            o.Scope.Clear(); o.Scope.Add("openid"); o.Scope.Add("email"); o.Scope.Add("profile");
            o.SaveTokens = false; o.GetClaimsFromUserInfoEndpoint = false; o.MapInboundClaims = false; o.TimeProvider = clock;
            o.RemoteAuthenticationTimeout = TimeSpan.FromMinutes(10); o.BackchannelTimeout = TimeSpan.FromSeconds(10);
            o.PushedAuthorizationBehavior = PushedAuthorizationBehavior.Disable;
            o.CorrelationCookie.SecurePolicy = CookieSecurePolicy.Always; o.NonceCookie.SecurePolicy = CookieSecurePolicy.Always;
            o.TokenValidationParameters = new TokenValidationParameters { ValidateIssuer = true, ValidIssuers = ["https://accounts.google.com", "accounts.google.com"],
                ValidateAudience = true, ValidAudience = o.ClientId, ValidateIssuerSigningKey = true, RequireSignedTokens = true,
                RequireExpirationTime = true, ValidateLifetime = true, ValidAlgorithms = [SecurityAlgorithms.RsaSha256], ClockSkew = TimeSpan.FromSeconds(30),
                LifetimeValidator = (nbf, exp, _, _) => Security.Authentication.AccessTokenRules.Lifetime(nbf, exp, clock.GetUtcNow(), 30) };
            o.Events.OnRedirectToIdentityProvider = context =>
            {
                context.ProtocolMessage.RedirectUri = settings.CallbackUri;
                context.ProtocolMessage.Prompt = "select_account";
                if (context.Properties.GetString("google-flow") == GoogleFlow.Reauthenticate.ToString()) context.ProtocolMessage.MaxAge = "0";
                context.Response.Headers.CacheControl = "no-store"; context.Response.Headers["Referrer-Policy"] = "no-referrer";
                return Task.CompletedTask;
            };
            o.Events.OnAuthorizationCodeReceived = context =>
            { context.TokenEndpointRequest!.RedirectUri = settings.CallbackUri; return Task.CompletedTask; };
            o.Events.OnTicketReceived = async context =>
            {
                context.HandleResponse();
                var browser = context.HttpContext.RequestServices.GetRequiredService<GoogleBrowserContext>();
                try
                {
                    var binding = browser.Read(context.Request, false);
                    if (context.Properties?.GetString("google-context") != binding.ContextId.ToString("N")
                        || context.Properties.GetString("google-flow") != binding.Flow.ToString()) throw new AppException("AUTH_GOOGLE_INVALID");
                    var identity = GoogleIdentity.Read(context.Principal!);
                    var grant = await context.HttpContext.RequestServices.GetRequiredService<GoogleService>()
                        .StageAsync(identity, binding, context.HttpContext.TraceIdentifier, context.HttpContext.RequestAborted);
                    browser.Complete(context.Response, grant.Binding);
                    context.Response.Headers.CacheControl = "no-store"; context.Response.Headers["Referrer-Policy"] = "no-referrer";
                    context.Response.Redirect(settings.CompletionUrl + "#ticket=" + Uri.EscapeDataString(grant.Ticket));
                }
                catch (Exception ex)
                {
                    browser.Clear(context.Response);
                    var code = ex is AppException app ? app.Error.Code : "SERVICE_UNAVAILABLE";
                    SafeLog(context.HttpContext, code); RedirectError(context.HttpContext, settings, code);
                }
            };
            o.Events.OnRemoteFailure = context =>
            {
                context.HandleResponse(); context.HttpContext.RequestServices.GetRequiredService<GoogleBrowserContext>().Clear(context.Response);
                if (!settings.Enabled) return context.HttpContext.RequestServices.GetRequiredService<ApiProblems>()
                    .WriteAsync(context.HttpContext, context.HttpContext.RequestServices.GetRequiredService<ApiProblems>().Create(context.HttpContext, ApiErrors.Get("SERVICE_UNAVAILABLE")));
                SafeLog(context.HttpContext, "AUTH_GOOGLE_INVALID");
                RedirectError(context.HttpContext, settings, "AUTH_GOOGLE_INVALID"); return Task.CompletedTask;
            };
        });
        return services;
    }
    private static bool SafeUrl(string value) => value.Length <= 2048 && Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0;
    private static void SafeLog(HttpContext context, string code) => context.RequestServices.GetRequiredService<ILoggerFactory>()
        .CreateLogger("AuthProject.GoogleProtocol").LogWarning("Google authentication failed ({ErrorCode}); trace {TraceId}.", code, context.TraceIdentifier);
    private static void RedirectError(HttpContext context, GoogleOptions settings, string code)
    {
        context.Response.Headers.CacheControl = "no-store"; context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Redirect(settings.ErrorUrl + "#error=" + Uri.EscapeDataString(code) + "&traceId=" + Uri.EscapeDataString(context.TraceIdentifier));
    }
}
