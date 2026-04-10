using Cocoar.Auth.Application.DTOs.Common;
using ErrorOr;
using Microsoft.AspNetCore.Mvc;

namespace Cocoar.Auth.Api.Controllers;

[ApiController]
public abstract class ApiControllerBase : ControllerBase
{
    protected IActionResult Problem(List<Error> errors)
    {
        if (errors.Count == 0)
        {
            return Problem();
        }

        if (errors.All(e => e.Type == ErrorType.Validation))
        {
            return ValidationProblem(errors);
        }

        return Problem(errors[0]);
    }

    protected IActionResult Problem(Error error)
    {
        var statusCode = error.Type switch
        {
            ErrorType.Conflict => StatusCodes.Status409Conflict,
            ErrorType.Validation => StatusCodes.Status400BadRequest,
            ErrorType.NotFound => StatusCodes.Status404NotFound,
            ErrorType.Unauthorized => StatusCodes.Status401Unauthorized,
            ErrorType.Forbidden => StatusCodes.Status403Forbidden,
            _ => StatusCodes.Status500InternalServerError
        };

        return Problem(
            statusCode: statusCode,
            title: error.Code,
            detail: error.Description);
    }

    private IActionResult ValidationProblem(List<Error> errors)
    {
        var modelStateDictionary = new Microsoft.AspNetCore.Mvc.ModelBinding.ModelStateDictionary();

        foreach (var error in errors)
        {
            modelStateDictionary.AddModelError(error.Code, error.Description);
        }

        return ValidationProblem(modelStateDictionary);
    }

    /// <summary>
    /// Returns the current tenant ID from the HTTP context.
    /// Used with IMessageBus.InvokeForTenantAsync to propagate tenant context to Wolverine handlers.
    /// </summary>
    protected string GetTenantId()
        => HttpContext.Items["TenantId"] as string
           ?? throw new InvalidOperationException("No tenant context available.");

    protected IActionResult FromErrorOr<T>(ErrorOr<T> result, Func<T, IActionResult>? successFunc = null)
    {
        if (result.IsError)
        {
            return Problem(result.Errors);
        }

        if (successFunc is not null)
        {
            return successFunc(result.Value);
        }

        return Ok(result.Value);
    }
}
