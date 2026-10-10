using AuthProject.Data;
using AuthProject.Entities;
using AuthProject.Logging;
using AuthProject.Repositories;
using AuthProject.Repositories.Interfaces;
using AuthProject.Security;
using AuthProject.Security.Interfaces;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace AuthProject.Configuration;

public sealed class PersistenceOptions
{
    public bool Enabled { get; set; }
    public string DataProtectionKeyDirectory { get; set; } = "data-protection-keys";
}

public sealed class AuditLogOptions
{
    public bool Enabled { get; set; }
    public string Directory { get; set; } = "Logs";
    public string InstanceId { get; set; } = Guid.NewGuid().ToString("N");
    public int MaximumFileSizeMb { get; set; } = 10;
    public int MaxBackups { get; set; } = 10;
    public int FileRetentionDays { get; set; } = 30;
}

public static class PersistenceServices
{
    public static IServiceCollection AddAuthPersistence(this IServiceCollection services, IConfiguration configuration,
        IHostEnvironment environment)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddOptions<PersistenceOptions>().Bind(configuration.GetSection("Persistence"))
            .Validate<IConfiguration>((o, c) => !o.Enabled || ValidConnection(c.GetConnectionString("DefaultConnection")),
                "Enabled Persistence requires a valid DefaultConnection with server and database.")
            .Validate(o => !o.Enabled || !string.IsNullOrWhiteSpace(o.DataProtectionKeyDirectory),
                "Enabled Persistence requires a Data Protection key directory.").ValidateOnStart();
        services.AddOptions<PasswordHasherOptions>().Configure(o =>
            {
                o.CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3;
                o.IterationCount = 210_000;
            }).Bind(configuration.GetSection("Security:PasswordHash"))
            .Validate(o => o.CompatibilityMode == PasswordHasherCompatibilityMode.IdentityV3
                && o.IterationCount is >= 210_000 and <= 1_000_000,
                "Password hashing requires IdentityV3 and 210000–1000000 iterations.").ValidateOnStart();
        services.AddScoped<IPasswordHashService, PasswordHashService>();
        services.AddDbContextFactory<AuthDbContext>((provider, options) =>
        {
            if (!provider.GetRequiredService<IOptions<PersistenceOptions>>().Value.Enabled)
                throw new InvalidOperationException("Persistence is disabled. Enable it explicitly before accessing the store.");
            options.UseSqlServer(provider.GetRequiredService<IConfiguration>().GetConnectionString("DefaultConnection"));
            // No sensitive logging, automatic migrations, or transparent transaction replay.
        }, ServiceLifetime.Scoped);
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IRoleRepository, RoleRepository>();
        services.AddScoped<ISessionRepository, SessionRepository>();
        services.AddScoped<IRefreshTokenRepository, RefreshTokenRepository>();
        services.AddScoped<IAuthUnitOfWork, AuthUnitOfWork>();
        var dataProtection = services.AddDataProtection().SetApplicationName("AuthProject");
        // Key store is only activated when persistence is enabled; no writes during scaffold startup.
        if (configuration.GetValue<bool>("Persistence:Enabled"))
        {
            var directory = configuration["Persistence:DataProtectionKeyDirectory"] ?? "data-protection-keys";
            if (!string.IsNullOrWhiteSpace(directory))
                dataProtection.PersistKeysToFileSystem(new DirectoryInfo(Path.GetFullPath(directory, environment.ContentRootPath)));
        }
        services.AddSingleton<IAuditPayloadProtector, AuditPayloadProtector>();
        services.AddScoped<IAuditWriter, AuditWriter>();
        services.AddOptions<AuditLogOptions>().Bind(configuration.GetSection("Audit:Log4Net"));
        services.AddSingleton<IAuditSink>(provider => Log4NetAuditSink.Create(
            provider.GetRequiredService<IOptions<AuditLogOptions>>().Value, environment.ContentRootPath,
            Path.Combine(AppContext.BaseDirectory, "Logging", "log4net.config")));
        return services;
    }

    private static bool ValidConnection(string? connection)
    {
        if (string.IsNullOrWhiteSpace(connection)) return false;
        try
        {
            var parsed = new SqlConnectionStringBuilder(connection);
            return !string.IsNullOrWhiteSpace(parsed.DataSource) && !string.IsNullOrWhiteSpace(parsed.InitialCatalog);
        }
        catch (ArgumentException) { return false; }
    }
}
