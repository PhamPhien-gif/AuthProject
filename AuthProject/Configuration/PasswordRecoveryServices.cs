using AuthProject.BackgroundJobs;
using AuthProject.Repositories;
using AuthProject.Repositories.Interfaces;
using AuthProject.Services;
using AuthProject.Services.Email;
using AuthProject.Services.Interfaces;
using Microsoft.Extensions.Options;

namespace AuthProject.Configuration;

public sealed class PasswordRecoveryOptions
{
    public bool Enabled { get; set; }
    public string ResetUrl { get; set; } = string.Empty;
    public int TokenLifetimeSeconds { get; set; } = 1800;
    public int ResendCooldownSeconds { get; set; } = 60;
    public int SendWindowSeconds { get; set; } = 900;
    public int MaxSendsPerWindow { get; set; } = 3;
    public int RecentAuthenticationSeconds { get; set; } = 300;
}

public static class PasswordRecoveryServices
{
    public static IServiceCollection AddPasswordRecovery(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<PasswordRecoveryOptions>().Bind(configuration.GetSection("Auth:PasswordRecovery"))
            .Validate<IOptions<PersistenceOptions>, IOptions<EmailOptions>>((o, db, email) => !o.Enabled || db.Value.Enabled && email.Value.Enabled,
                "Enabled password recovery requires persistence and SMTP email.")
            .Validate(o => !o.Enabled || o.ResetUrl.Length <= 2048 && Uri.TryCreate(o.ResetUrl, UriKind.Absolute, out var uri)
                && uri.Scheme == "https" && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0
                && o.TokenLifetimeSeconds is >= 300 and <= 3600 && o.ResendCooldownSeconds is >= 30 and <= 900
                && o.SendWindowSeconds is >= 900 and <= 86400 && o.MaxSendsPerWindow is >= 1 and <= 10
                && o.RecentAuthenticationSeconds is >= 60 and <= 300,
                "Enabled password recovery requires HTTPS reset URL and bounded token/send/recent-auth limits.").ValidateOnStart();
        services.AddOptions<RegistrationOptions>().Validate<IConfiguration>((o, config) => !config.GetValue<bool>("Auth:PasswordRecovery:Enabled")
            || o.PollSeconds is >= 1 and <= 60 && o.SmtpTimeoutSeconds is >= 5 and <= 60
                && o.LeaseSeconds >= o.SmtpTimeoutSeconds + 60 && o.LeaseSeconds <= 600,
            "Password recovery requires bounded email delivery poll/timeout/lease options.");
        services.AddSingleton<IPasswordEmailProtector, PasswordEmailProtector>();
        services.AddScoped<IPasswordRecoveryStore, PasswordRecoveryStore>();
        services.AddScoped(provider => new Lazy<IPasswordRecoveryStore>(() => provider.GetRequiredService<IPasswordRecoveryStore>()));
        services.AddScoped(provider => new Lazy<AuthProject.Security.Interfaces.IPasswordCredentialGate>(() => provider.GetRequiredService<AuthProject.Security.Interfaces.IPasswordCredentialGate>()));
        services.AddScoped<IPasswordRecoveryService, PasswordRecoveryService>();
        services.AddScoped<PasswordEmailDispatcher>();
        services.AddHostedService<PasswordEmailWorker>();
        return services;
    }
}
