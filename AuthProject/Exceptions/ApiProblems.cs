using System.Text.Json;
using FluentValidation.Results;
using Microsoft.AspNetCore.Mvc;

namespace AuthProject.Exceptions;

public sealed record FieldError(string Code, string Message);

public sealed class ApiProblems
{
    public ProblemDetails Create(HttpContext context, ApiError error,
        IReadOnlyDictionary<string, FieldError[]>? fields = null)
    {
        var problem = new ProblemDetails
        {
            Type = error.Type, Title = error.Title, Status = error.Status,
            Instance = context.Request.PathBase + context.Request.Path
        };
        problem.Extensions["code"] = error.Code;
        problem.Extensions["traceId"] = context.TraceIdentifier;
        if (fields is not null) problem.Extensions["errors"] = fields;
        return problem;
    }

    public ProblemDetails Validation(HttpContext context, IEnumerable<ValidationFailure> failures) => Create(
        context, ApiErrors.ValidationFailed, failures.GroupBy(f => JsonNamingPolicy.CamelCase.ConvertName(f.PropertyName))
            .ToDictionary(group => group.Key, group => group.Select(f => new FieldError(f.ErrorCode, f.ErrorMessage)).Distinct().ToArray()));

    public async Task WriteAsync(HttpContext context, ProblemDetails problem, CancellationToken cancellationToken = default)
    {
        if (context.Response.HasStarted) return;
        context.Response.StatusCode = problem.Status ?? 500;
        context.Response.ContentType = "application/problem+json";
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["X-Trace-Id"] = context.TraceIdentifier;
        context.Response.ContentLength = null;
        // Preserve protocol headers already set by auth/rate-limit/routing middleware.
        await context.Response.WriteAsJsonAsync(problem, options: null,
            contentType: "application/problem+json", cancellationToken: cancellationToken);
    }
}

public sealed class ApiProblemResult(ProblemDetails problem) : IActionResult
{
    public Task ExecuteResultAsync(ActionContext context) => context.HttpContext.RequestServices
        .GetRequiredService<ApiProblems>().WriteAsync(context.HttpContext, problem, context.HttpContext.RequestAborted);
}

public sealed class ApiProblemDetailsWriter(ApiProblems problems) : IProblemDetailsWriter
{
    public bool CanWrite(ProblemDetailsContext context) => true;
    public ValueTask WriteAsync(ProblemDetailsContext context)
    {
        // Sanitize arbitrary ProblemDetails supplied by framework components.
        var status = context.ProblemDetails.Status ?? context.HttpContext.Response.StatusCode;
        return new ValueTask(problems.WriteAsync(context.HttpContext,
            problems.Create(context.HttpContext, ApiErrors.ForStatus(status)), context.HttpContext.RequestAborted));
    }
}
