using System.ComponentModel.DataAnnotations;
using AuthProject.DTOs.Validators;
using AuthProject.Exceptions;
using AuthProject.Filters;
using AuthProject.Middlewares;
using FluentValidation;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.Extensions.Options;

namespace AuthProject.Configuration;

public static class FoundationServices
{
    public static IServiceCollection AddApiFoundation(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<ApiProblems>();
        services.AddSingleton<IProblemDetailsWriter, ApiProblemDetailsWriter>();
        services.AddProblemDetails();
        services.AddExceptionHandler<ApiExceptionHandler>();
        services.AddValidatorsFromAssemblyContaining<RegisterRequestValidator>();
        services.AddScoped<AsyncValidationFilter>();
        services.AddScoped<ApiErrorResultFilter>();
        services.AddControllers(options =>
        {
            options.Filters.AddService<AsyncValidationFilter>();
            options.Filters.AddService<ApiErrorResultFilter>();
            options.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true;
        }).ConfigureApiBehaviorOptions(options =>
        {
            options.SuppressMapClientErrors = true;
            options.InvalidModelStateResponseFactory = context =>
            {
                var problems = context.HttpContext.RequestServices.GetRequiredService<ApiProblems>();
                // Never echo JSON parser errors, attempted values, or raw model-state messages.
                return new ApiProblemResult(problems.Create(context.HttpContext, ApiErrors.InvalidRequest,
                    new Dictionary<string, FieldError[]>
                    {
                        ["request"] = [new("INVALID_REQUEST", "Body hoặc tham số không hợp lệ.")]
                    }));
            };
        });
        services.AddOptions<ApiOptions>().Bind(configuration.GetSection(ApiOptions.Section))
            .Validate(o => o.MaxRequestBodyBytes is >= 1024 and <= 1_048_576, "Api:MaxRequestBodyBytes must be between 1024 and 1048576.")
            .Validate(o => o.RequestTimeoutSeconds is >= 1 and <= 120, "Api:RequestTimeoutSeconds must be between 1 and 120.")
            .ValidateOnStart();
        services.AddOptions<JwtOptions>().Bind(configuration.GetSection("Auth:Jwt"))
            .Validate(o => !o.Enabled || (Present(o.Issuer, o.Audience, o.SigningKeyPath, o.KeyId)
                && o.AccessTokenLifetimeSeconds is >= 60 and <= 900 && o.ClockSkewSeconds is >= 0 and <= 30),
                "Enabled Auth:Jwt requires issuer, audience, signing key path, key ID and valid lifetimes.").ValidateOnStart();
        services.AddOptions<EmailOptions>().Bind(configuration.GetSection("Auth:Email"))
            .Validate(o => !o.Enabled || (Present(o.Host, o.Sender, o.Username, o.Password) && o.Port is >= 1 and <= 65535
                && o.UseTls && new EmailAddressAttribute().IsValid(o.Sender)),
                "Enabled Auth:Email requires SMTP host, port, TLS, sender and credentials.").ValidateOnStart();
        services.AddOptions<GoogleOptions>().Bind(configuration.GetSection("Auth:Google"))
            .Validate(o => !o.Enabled || (Present(o.ClientId, o.ClientSecret, o.CallbackPath) && o.CallbackPath.StartsWith('/')
                && !o.CallbackPath.StartsWith("//") && !o.CallbackPath.Contains('?') && !o.CallbackPath.Contains('#')),
                "Enabled Auth:Google requires client credentials and a local callback path.").ValidateOnStart();
        services.AddOptions<EmailOtpOptions>().Bind(configuration.GetSection("Auth:EmailOtp"))
            .Validate<IOptions<EmailOptions>>((o, email) => !o.Enabled || (email.Value.Enabled && Present(o.KeyId)
                && IsStrongBase64(o.Pepper) && o.ExpirySeconds is >= 60 and <= 600 && o.MaxAttempts is >= 1 and <= 10),
                "Enabled Auth:EmailOtp requires enabled email, a Base64 pepper of at least 32 bytes, key ID and valid limits.").ValidateOnStart();
        services.AddRequestTimeouts();
        services.AddOptions<RequestTimeoutOptions>().Configure<IOptions<ApiOptions>>((options, api) =>
            options.DefaultPolicy = new RequestTimeoutPolicy
            {
                Timeout = TimeSpan.FromSeconds(api.Value.RequestTimeoutSeconds),
                TimeoutStatusCode = StatusCodes.Status503ServiceUnavailable,
                WriteTimeoutResponse = context =>
                {
                    var problems = context.RequestServices.GetRequiredService<ApiProblems>();
                    return problems.WriteAsync(context, problems.Create(context, ApiErrors.ForStatus(503)));
                }
            });
        return services;
    }

    private static bool Present(params string[] values) => values.All(value => !string.IsNullOrWhiteSpace(value));
    private static bool IsStrongBase64(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 512) return false;
        Span<byte> buffer = stackalloc byte[384];
        return Convert.TryFromBase64String(value, buffer, out var length) && length >= 32;
    }
}
