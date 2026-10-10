using AuthProject.Configuration;
using AuthProject.Services;
using Microsoft.Extensions.Options;

namespace AuthProject.BackgroundJobs;

public sealed class AuditDeliveryWorker(IServiceScopeFactory scopes, IOptions<AuditDeliveryOptions> options, ILogger<AuditDeliveryWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!options.Value.Enabled) return;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope(); var dispatcher = scope.ServiceProvider.GetRequiredService<AuditDispatcher>();
                for (var i = 0; i < 10 && await dispatcher.DispatchOneAsync(ct); i++) { }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception) { log.LogError("Audit delivery worker unavailable; durable outbox remains pending."); }
            try { await Task.Delay(TimeSpan.FromSeconds(options.Value.PollSeconds), ct); } catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        }
    }
}
public sealed class AuditRetentionWorker(IServiceScopeFactory scopes, IOptions<AuditRetentionOptions> options, ILogger<AuditRetentionWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!options.Value.Enabled) return;
        while (!ct.IsCancellationRequested)
        {
            try { await using var scope = scopes.CreateAsyncScope(); await scope.ServiceProvider.GetRequiredService<AuditRetentionService>().SweepAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception) { log.LogError("Audit retention failed; cleanup will retry."); }
            try { await Task.Delay(TimeSpan.FromSeconds(options.Value.PollSeconds), ct); } catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        }
    }
}
