using AuthProject.Configuration;
using AuthProject.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;

namespace AuthProject.Filters;

[AttributeUsage(AttributeTargets.Method)]
public sealed class CookieCsrfAttribute() : TypeFilterAttribute(typeof(CookieCsrfFilter));

public sealed class CookieCsrfFilter(BrowserCsrfGuard guard, IOptions<RefreshOptions> options) : IAsyncAuthorizationFilter
{
    public Task OnAuthorizationAsync(AuthorizationFilterContext context) => options.Value.Enabled
        ? guard.ValidateAsync(context.HttpContext) : Task.CompletedTask;
}
