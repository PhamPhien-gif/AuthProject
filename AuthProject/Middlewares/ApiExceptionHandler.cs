using AuthProject.Exceptions;
using Microsoft.AspNetCore.Diagnostics;

namespace AuthProject.Middlewares;

public sealed class ApiExceptionHandler(ApiProblems problems, ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        if (context.RequestAborted.IsCancellationRequested) return false;
        var error = exception switch
        {
            AppException appException => appException.Error,
            BadHttpRequestException badRequest => ApiErrors.ForStatus(badRequest.StatusCode),
            _ => ApiErrors.Internal
        };
        // Exception.Message/Data/StackTrace may contain credentials or SQL: do not log the exception object.
        logger.Log(error.Status >= 500 ? LogLevel.Error : LogLevel.Warning,
            "Request failed with {ErrorCode}; exception type {ExceptionType}; trace {TraceId}",
            error.Code, exception.GetType().Name, context.TraceIdentifier);
        await problems.WriteAsync(context, problems.Create(context, error), cancellationToken);
        return true;
    }
}
