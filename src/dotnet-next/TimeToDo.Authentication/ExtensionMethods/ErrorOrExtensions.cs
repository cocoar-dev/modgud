using ErrorOr;

namespace TimeToDo.Authentication.ExtensionMethods;

public static class ErrorOrExtensions
{
    public static IResult ToResult<T>(this ErrorOr<T> errorOr, Func<T, IResult>? onSuccess = null)
    {
        if (errorOr.IsError)
        {
            return ToErrorResult(errorOr.Errors);
        }

        return onSuccess != null
            ? onSuccess(errorOr.Value)
            : Results.Ok(errorOr.Value);
    }

    public static IResult ToCreatedResult<T>(this ErrorOr<T> errorOr, Func<T, string> locationFactory)
    {
        if (errorOr.IsError)
        {
            return ToErrorResult(errorOr.Errors);
        }

        return Results.Created(locationFactory(errorOr.Value), errorOr.Value);
    }

    public static IResult ToNoContentResult(this ErrorOr<Success> errorOr)
    {
        if (errorOr.IsError)
        {
            return ToErrorResult(errorOr.Errors);
        }

        return Results.NoContent();
    }

    private static IResult ToErrorResult(List<Error> errors)
    {
        if (errors.Count == 0)
        {
            return Results.Problem();
        }

        var firstError = errors[0];

        return firstError.Type switch
        {
            ErrorType.NotFound => Results.NotFound(new { error = firstError.Description }),
            ErrorType.Validation => Results.BadRequest(new { error = firstError.Description }),
            ErrorType.Unauthorized => Results.Unauthorized(),
            ErrorType.Forbidden => Results.Forbid(),
            ErrorType.Conflict => Results.Conflict(new { error = firstError.Description }),
            _ => Results.Problem(detail: firstError.Description, statusCode: 500)
        };
    }
}
