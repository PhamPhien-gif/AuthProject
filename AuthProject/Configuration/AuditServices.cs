using AuthProject.BackgroundJobs;
using AuthProject.Repositories;
using AuthProject.Services;
using Microsoft.Extensions.Options;

namespace AuthProject.Configuration;

public sealed class AuditDeliveryOptions
{
    public bool Enabled { get; set; }
    public int PollSeconds { get; set; } = 5;
    public int LeaseSeconds { get; set; } = 120;
    public int MaxAttempts { get; set; } = 10;
}
public sealed class AuditRetentionOptions
{
    public bool Enabled { get; set; }
    public int PollSeconds { get; set; } = 3600;
    public int BatchSize { get; set; } = 100;
    public int LoginHistoryDays { get; set; } = 90;
    public int AuditDays { get; set; } = 365;
    public int TokenGraceDays { get; set; } = 7;
    public int ProcessedOutboxDays { get; set; } = 7;
}
public static class AuditServices
{
    public static IServiceCollection AddAuditOperations(this IServiceCollection services, IConfiguration config)
    {
        services.AddOptions<AuditLogOptions>().Validate(o => !o.Enabled || !string.IsNullOrWhiteSpace(o.Directory)
            && o.InstanceId.Length is > 0 and <= 64 && o.InstanceId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
            && o.MaximumFileSizeMb is >= 1 and <= 100 && o.MaxBackups is >= 1 and <= 100 && o.FileRetentionDays is >= 1 and <= 365,
            "Audit file options require a directory, safe instance ID and bounded retention/size.").ValidateOnStart();
        services.AddOptions<AuditDeliveryOptions>().Bind(config.GetSection("Audit:Delivery"))
            .Validate<IOptions<PersistenceOptions>, IOptions<AuditLogOptions>>((o, p, file) => !o.Enabled || p.Value.Enabled && file.Value.Enabled,
                "Audit delivery requires persistence and log4net enabled.")
            .Validate(o => o.PollSeconds is >= 1 and <= 60 && o.LeaseSeconds is >= 30 and <= 600 && o.MaxAttempts is >= 1 and <= 20,
                "Audit delivery requires bounded polling, lease and retry limits.").ValidateOnStart();
        services.AddOptions<AuditRetentionOptions>().Bind(config.GetSection("Audit:Retention"))
            .Validate<IOptions<PersistenceOptions>>((o, p) => !o.Enabled || p.Value.Enabled, "Audit retention requires persistence.")
            .Validate(o => o.PollSeconds is >= 60 and <= 86400 && o.BatchSize is >= 1 and <= 1000
                && o.LoginHistoryDays is >= 30 and <= 3650 && o.AuditDays is >= 90 and <= 3650
                && o.TokenGraceDays is >= 1 and <= 30 && o.ProcessedOutboxDays is >= 1 and <= 90,
                "Audit retention requires bounded retention periods and batch size.").ValidateOnStart();
        services.AddScoped<AuditQueryStore>(); services.AddScoped(p => new Lazy<AuditQueryStore>(() => p.GetRequiredService<AuditQueryStore>()));
        services.AddScoped<AuditQueryService>(); services.AddScoped<AuditDispatcher>(); services.AddScoped<AuditRetentionService>();
        services.AddHostedService<AuditDeliveryWorker>(); services.AddHostedService<AuditRetentionWorker>();
        return services;
    }
}
