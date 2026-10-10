using AuthProject.DTOs;
using AuthProject.Exceptions;
using FluentValidation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace AuthProject.IntegrationTests.Foundation;

// Registered via ApplicationPart ONLY in the test factory. Never shipped in the API.
[ApiController]
[Microsoft.AspNetCore.Authorization.AllowAnonymous]
[Route("_tests/foundation")]
public sealed class ProbeController : ControllerBase
{
    [HttpPost("register")]
    public IActionResult Register(RegisterRequest request) => NoContent();

    [HttpGet("query")]
    public IActionResult Query([FromQuery] PagingRequest request) => NoContent();

    [HttpGet("failure")]
    public IActionResult Failure() => throw new InvalidOperationException("DO_NOT_LEAK_SECRET_OR_SQL");

    [HttpGet("status/{status:int}")]
    public IActionResult Status(int status)
    {
        if (status == 401) Response.Headers.WWWAuthenticate = "Bearer";
        if (status == 429) Response.Headers.RetryAfter = "60";
        return StatusCode(status);
    }

    [HttpGet("body-error")]
    public IActionResult BodyError() => BadRequest(new { secret = "DO_NOT_LEAK" });

    [HttpGet("expected-error")]
    public IActionResult ExpectedError() => throw new AppException("RESOURCE_CONFLICT");

    [HttpGet("framework-problem")]
    public async Task FrameworkProblem([FromServices] IProblemDetailsService problems) => await problems.WriteAsync(
        new ProblemDetailsContext
        {
            HttpContext = HttpContext,
            ProblemDetails = new ProblemDetails { Status = 400, Detail = "DO_NOT_LEAK" }
        });

    [HttpPost("async")]
    public IActionResult Async(AsyncRequest request) => NoContent();

    [HttpGet("timeout")]
    public async Task<IActionResult> Timeout()
    {
        await Task.Delay(System.Threading.Timeout.Infinite, HttpContext.RequestAborted);
        return NoContent();
    }
}

public sealed record AsyncRequest(string Value);
public sealed class AsyncRequestValidator : AbstractValidator<AsyncRequest>
{
    public AsyncRequestValidator() => RuleFor(x => x.Value).MustAsync(async (value, cancellationToken) =>
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        return value == "allowed";
    }).WithErrorCode("ASYNC_REJECTED").WithMessage("Value bị từ chối.");
}
