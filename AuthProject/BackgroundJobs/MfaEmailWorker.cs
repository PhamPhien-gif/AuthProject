using AuthProject.Configuration;
using AuthProject.Services.Email;
using Microsoft.Extensions.Options;

namespace AuthProject.BackgroundJobs;

public sealed class MfaEmailWorker(IServiceScopeFactory scopes, IOptions<EmailOtpOptions> recovery,
    IOptions<RegistrationOptions> delivery, ILogger<MfaEmailWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!recovery.Value.Enabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var dispatcher = scope.ServiceProvider.GetRequiredService<MfaEmailDispatcher>();
                for (var count = 0; count < 10 && await dispatcher.DispatchOneAsync(stoppingToken); count++) { }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogWarning("MFA email worker failed ({FailureType}).", ex.GetType().Name); }
            try { await Task.Delay(TimeSpan.FromSeconds(delivery.Value.PollSeconds), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        }
    }
}

