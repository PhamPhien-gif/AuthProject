using AuthProject.DTOs;
using AuthProject.Exceptions;
using AuthProject.Security.Interfaces;
using FluentValidation;

namespace AuthProject.Services;

public static class AdminBootstrapCommand
{
    // Only process environment, never command-line password, appsettings, logs or a public endpoint.
    public static async Task<int> RunAsync(IServiceProvider services)
    {
        try
        {
            var request = new RegisterRequest(Environment.GetEnvironmentVariable("AUTHPROJECT_BOOTSTRAP_ADMIN_EMAIL"),
                Environment.GetEnvironmentVariable("AUTHPROJECT_BOOTSTRAP_ADMIN_PASSWORD"),
                Environment.GetEnvironmentVariable("AUTHPROJECT_BOOTSTRAP_ADMIN_DISPLAY_NAME"));
            await using var scope = services.CreateAsyncScope();
            var result = await scope.ServiceProvider.GetRequiredService<AdminService>().BootstrapAsync(request,
                scope.ServiceProvider.GetRequiredService<IValidator<RegisterRequest>>(), scope.ServiceProvider.GetRequiredService<IPasswordHashService>(),
                Guid.NewGuid().ToString("N"), default);
            Console.WriteLine(result.Created ? "Admin created. Enroll MFA before using admin APIs." : "Admin already exists. Credentials and MFA were preserved.");
            return 0;
        }
        catch (AppException ex) { Console.Error.WriteLine("Bootstrap failed: " + ex.Error.Code); return 1; }
        catch (Exception) { Console.Error.WriteLine("Bootstrap failed: INTERNAL_ERROR"); return 1; }
    }
}
