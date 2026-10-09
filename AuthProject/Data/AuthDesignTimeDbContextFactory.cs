using AuthProject.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AuthProject.Data;

public sealed class AuthDesignTimeDbContextFactory : IDesignTimeDbContextFactory<AuthDbContext>
{
    public AuthDbContext CreateDbContext(string[] args)
    {
        using var configuration = new ConfigurationManager();
        var environment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? Environments.Development;
        var directory = Directory.GetCurrentDirectory();
        if (LocalDotEnv.FindFile(directory) is null)
            directory = Path.GetDirectoryName(typeof(AuthDbContext).Assembly.Location)!;
        LocalDotEnv.AddToConfiguration(configuration, environment, directory);
        configuration.AddEnvironmentVariables();
        var connection = configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(connection))
            throw new InvalidOperationException("Configure ConnectionStrings__DefaultConnection through environment variables or a local .env file (Development).");
        return new AuthDbContext(new DbContextOptionsBuilder<AuthDbContext>().UseSqlServer(connection).Options);
    }
}
