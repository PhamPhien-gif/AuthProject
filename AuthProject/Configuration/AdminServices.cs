using AuthProject.Data.Configurations;
using AuthProject.Repositories;
using AuthProject.Security.Authorization;
using AuthProject.Services;
using Microsoft.AspNetCore.Authorization;

namespace AuthProject.Configuration;

public static class AdminServices
{
    public static IServiceCollection AddAdminManagement(this IServiceCollection services)
    {
        services.AddScoped<AdminStore>();
        services.AddScoped(p => new Lazy<AdminStore>(() => p.GetRequiredService<AdminStore>()));
        services.AddScoped<AdminService>();
        services.AddScoped<IAuthorizationHandler, AdminAuthorizationHandler>();
        services.AddAuthorization(options =>
        {
            options.AddPolicy(AuthPolicies.AdminRead, p => p.RequireAuthenticatedUser().RequireRole(SystemRoles.Admin).AddRequirements(new AdminRequirement(false)));
            options.AddPolicy(AuthPolicies.AdminWrite, p => p.RequireAuthenticatedUser().RequireRole(SystemRoles.Admin).AddRequirements(new AdminRequirement(true)));
        });
        return services;
    }
}
