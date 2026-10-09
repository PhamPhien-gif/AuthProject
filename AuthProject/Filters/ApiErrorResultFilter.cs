using AuthProject.Exceptions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace AuthProject.Filters;

// Prevent ad-hoc BadRequest/Problem/Json/Content error payloads from bypassing the contract.
public sealed class ApiErrorResultFilter(ApiProblems problems) : IAlwaysRunResultFilter
{
    public void OnResultExecuting(ResultExecutingContext context)
    {
        var status = context.Result switch
        {
            ObjectResult result => result.StatusCode ?? (result.Value as ProblemDetails)?.Status,
            JsonResult result => result.StatusCode,
            ContentResult result => result.StatusCode,
            _ => null
        };
        if (status is >= 400)
            context.Result = new ApiProblemResult(problems.Create(context.HttpContext, ApiErrors.ForStatus(status.Value)));
    }

    public void OnResultExecuted(ResultExecutedContext context) { }
}
