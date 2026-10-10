using AuthProject.BackgroundJobs;
using AuthProject.Repositories;
using AuthProject.Repositories.Interfaces;
using AuthProject.Services;
using AuthProject.Services.Email;
using AuthProject.Services.Interfaces;
using Microsoft.Extensions.Options;

namespace AuthProject.Configuration;

public static class RegistrationServices
{
    public static IServiceCollection AddRegistration(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<RegistrationOptions>().Bind(configuration.GetSection("Auth:Registration"))
            .Validate<IOptions<PersistenceOptions>, IOptions<EmailOptions>>((o, persistence, email) => !o.Enabled || (persistence.Value.Enabled && email.Value.Enabled),
                "Enabled registration requires persistence and SMTP email.")
            .Validate(o => !o.Enabled || (Uri.TryCreate(o.VerificationUrl, UriKind.Absolute, out var uri) && uri.Scheme == "https"
                && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment)
                && o.TokenLifetimeSeconds is >= 300 and <= 86400 && o.ResendCooldownSeconds is >= 30 and <= 900
                && o.SendWindowSeconds is >= 900 and <= 86400 && o.MaxSendsPerWindow is >= 1 and <= 10
                && o.PollSeconds is >= 1 and <= 60 && o.SmtpTimeoutSeconds is >= 5 and <= 60
                && o.LeaseSeconds >= o.SmtpTimeoutSeconds + 60 && o.LeaseSeconds <= 600),
                "Enabled registration requires HTTPS verification URL and bounded token/send/worker limits.").ValidateOnStart();
        services.AddSingleton<IEmailPayloadProtector, EmailPayloadProtector>();
        services.AddScoped<IEmailTransport, SmtpEmailTransport>();
        services.AddScoped<IRegistrationStore, RegistrationStore>();
        services.AddScoped<IRegistrationService>(provider => provider.GetRequiredService<IOptions<RegistrationOptions>>().Value.Enabled
            ? ActivatorUtilities.CreateInstance<RegistrationService>(provider) : new UnavailableRegistrationService());
        services.AddScoped<EmailOutboxDispatcher>();
        services.AddHostedService<VerificationEmailWorker>();
        return services;
    }
}

internal sealed class UnavailableRegistrationService : IRegistrationService
{
    public Task RegisterAsync(AuthProject.DTOs.RegisterRequest request, string traceId, CancellationToken cancellationToken = default)
        => throw new AuthProject.Exceptions.AppException("SERVICE_UNAVAILABLE");
    public Task ResendAsync(AuthProject.DTOs.EmailRequest request, string traceId, CancellationToken cancellationToken = default)
        => throw new AuthProject.Exceptions.AppException("SERVICE_UNAVAILABLE");
    public Task VerifyAsync(AuthProject.DTOs.VerifyEmailRequest request, string traceId, CancellationToken cancellationToken = default)
        => throw new AuthProject.Exceptions.AppException("SERVICE_UNAVAILABLE");
}
