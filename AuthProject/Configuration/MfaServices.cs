using AuthProject.BackgroundJobs;
using AuthProject.Repositories;
using AuthProject.Repositories.Interfaces;
using AuthProject.Security;
using AuthProject.Services;
using AuthProject.Services.Email;
using Microsoft.Extensions.Options;

namespace AuthProject.Configuration;

public static class MfaServices
{
    public static IServiceCollection AddEmailMfa(this IServiceCollection services)
    {
        services.AddOptions<EmailOtpOptions>()
            .Validate<IOptions<JwtOptions>, IOptions<RefreshOptions>>((o, jwt, refresh) => !o.Enabled || jwt.Value.Enabled && refresh.Value.Enabled,
                "Email MFA requires JWT and browser refresh/CSRF protection.")
            .Validate(o => !o.Enabled || o.KeyId.Length is >= 1 and <= 64 && o.KeyId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')
                && o.ChallengeLifetimeSeconds >= o.ExpirySeconds && o.ChallengeLifetimeSeconds <= 600
                && o.ResendCooldownSeconds is >= 30 and <= 300 && o.MaxSends is >= 1 and <= 5
                && o.RecentAuthenticationSeconds is >= 60 and <= 300 && o.RecoveryCodeCount is >= 8 and <= 16,
                "Email MFA requires bounded key ID, challenge/send/recent-auth/recovery limits.");
        services.AddOptions<RegistrationOptions>().Validate<IConfiguration>((o, c) => !c.GetValue<bool>("Auth:EmailOtp:Enabled")
            || o.PollSeconds is >= 1 and <= 60 && o.SmtpTimeoutSeconds is >= 5 and <= 60
                && o.LeaseSeconds >= o.SmtpTimeoutSeconds + 60 && o.LeaseSeconds <= 600,
                "Email MFA requires bounded delivery poll/timeout/lease limits.");
        services.AddSingleton<EmailOtpSecret>();
        services.AddSingleton<MfaContext>();
        services.AddSingleton<MfaEmailProtector>();
        services.AddScoped<MfaStore>();
        services.AddScoped(p => new Lazy<MfaStore>(() => p.GetRequiredService<MfaStore>()));
        services.AddScoped(p => new Lazy<ILoginStore>(() => p.GetRequiredService<ILoginStore>()));
        services.AddScoped<MfaService>();
        services.AddScoped<MfaEmailDispatcher>();
        services.AddHostedService<MfaEmailWorker>();
        return services;
    }
}
