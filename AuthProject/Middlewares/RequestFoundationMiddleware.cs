using AuthProject.Configuration;
using AuthProject.Exceptions;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

namespace AuthProject.Middlewares;

public sealed class RequestFoundationMiddleware(RequestDelegate next, ILogger<RequestFoundationMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context, IOptions<ApiOptions> options, ApiProblems problems)
    {
        context.Response.OnStarting(() =>
        {
            context.Response.Headers["X-Trace-Id"] = context.TraceIdentifier;
            return Task.CompletedTask;
        });
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["TraceId"] = context.TraceIdentifier });
        var limit = options.Value.MaxRequestBodyBytes;
        if (context.Request.ContentLength > limit)
        {
            await problems.WriteAsync(context, problems.Create(context, ApiErrors.ForStatus(413)), context.RequestAborted);
            return;
        }
        var bodyFeature = context.Features.Get<IHttpRequestBodyDetectionFeature>();
        if (bodyFeature?.CanHaveBody != true && context.Request.ContentLength is not > 0
            && !context.Request.Headers.ContainsKey("Transfer-Encoding"))
        {
            await next(context);
            return;
        }

        // Bound unknown-length/chunked bodies too. Never log the buffer or attempted input.
        var originalBody = context.Request.Body;
        using var body = new MemoryStream();
        var buffer = new byte[Math.Min(limit + 1, 8192)];
        while (body.Length <= limit)
        {
            var read = await originalBody.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, limit + 1 - (int)body.Length)), context.RequestAborted);
            if (read == 0) break;
            body.Write(buffer, 0, read);
        }
        if (body.Length > limit)
        {
            await problems.WriteAsync(context, problems.Create(context, ApiErrors.ForStatus(413)), context.RequestAborted);
            return;
        }
        body.Position = 0;
        context.Request.Body = body;
        try { await next(context); }
        finally { context.Request.Body = originalBody; }
    }
}
