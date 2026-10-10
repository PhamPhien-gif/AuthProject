using AuthProject.Configuration;
using AuthProject.Services.Email;
using Microsoft.Extensions.Options;

namespace AuthProject.BackgroundJobs;

public sealed class VerificationEmailWorker(IServiceScopeFactory scopes, IOptions<RegistrationOptions> options,
    ILogger<VerificationEmailWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var dispatcher = scope.ServiceProvider.GetRequiredService<EmailOutboxDispatcher>();
                for (var count = 0; count < 10 && await dispatcher.DispatchOneAsync(stoppingToken); count++) { }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogWarning("Verification email worker failed ({FailureType}).", ex.GetType().Name); }
            try { await Task.Delay(TimeSpan.FromSeconds(options.Value.PollSeconds), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        }
    }
}
