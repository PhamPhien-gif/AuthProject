using AuthProject.Filters;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace AuthProject.Security.Authentication;

public sealed class CookieCsrfSwaggerFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        if (!context.ApiDescription.ActionDescriptor.EndpointMetadata.OfType<CookieCsrfAttribute>().Any()) return;
        operation.Parameters ??= [];
        operation.Parameters.Add(new OpenApiParameter
        {
            Name = BrowserSessionCookies.CsrfHeader, In = ParameterLocation.Header, Required = false,
            Description = "Required when Auth:Refresh:Enabled=true. Obtain requestToken from GET /api/v1/auth/csrf in the same browser; keep its cookie.",
            Schema = new OpenApiSchema { Type = JsonSchemaType.String }
        });
    }
}
