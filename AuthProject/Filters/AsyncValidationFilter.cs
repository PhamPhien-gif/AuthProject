using AuthProject.Exceptions;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Mvc.Filters;

namespace AuthProject.Filters;

public sealed class AsyncValidationFilter(ApiProblems problems) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var failures = new List<ValidationFailure>();
        foreach (var parameter in context.ActionDescriptor.Parameters)
        {
            if (!context.ActionArguments.TryGetValue(parameter.Name, out var argument) || argument is null) continue;
            var validatorType = typeof(IValidator<>).MakeGenericType(parameter.ParameterType);
            foreach (var validator in context.HttpContext.RequestServices.GetServices(validatorType).Cast<IValidator>())
            {
                var result = await validator.ValidateAsync(new ValidationContext<object>(argument), context.HttpContext.RequestAborted);
                failures.AddRange(result.Errors);
            }
        }
        if (failures.Count > 0)
        {
            context.Result = new ApiProblemResult(problems.Validation(context.HttpContext, failures));
            return;
        }
        await next();
    }
}
