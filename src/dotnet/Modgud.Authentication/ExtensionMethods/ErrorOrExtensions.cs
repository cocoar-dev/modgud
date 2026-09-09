using ErrorOr;

namespace Modgud.Authentication.ExtensionMethods;

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

    /// <summary>
    /// The application's single error body: <c>{ "Error": "&lt;code&gt;", "Message": "&lt;description&gt;" }</c>.
    ///
    /// <para>It used to be <c>{ "error": "&lt;description&gt;" }</c>, which was wrong twice over.
    /// It dropped the machine-readable <see cref="Error.Code"/> entirely, so a client could only
    /// match on prose; and it put a description under a key named <c>error</c>, which is exactly
    /// the key OAuth reserves for an error CODE. Anything reading both surfaces — the frontend,
    /// <c>Modgud.Provisioning.TestKit</c> — got a description handed to it as if it were a code.
    /// The OAuth/OIDC/DCR endpoints keep their RFC-mandated <c>{ error, error_description }</c>
    /// and are untouched by this: they are a different contract, and the collision was ours.</para>
    ///
    /// <para>Forbidden and Unauthorized are rendered here too, rather than delegated to
    /// <see cref="Results.Forbid()"/> / <see cref="Results.Unauthorized()"/>. Under this app's
    /// cookie auth, <c>Forbid()</c> turns an <c>/api/*</c> response into an EMPTY-body 403, which
    /// throws away the very code the SPA needs — and that is why three endpoint files had each
    /// grown their own private copy of this renderer. Emitting the body directly is what lets
    /// them collapse back into one. The status codes are unchanged; only the body appears.</para>
    /// </summary>
    public static IResult ToErrorResult(List<Error> errors)
    {
        if (errors.Count == 0)
        {
            return Results.Problem();
        }

        var firstError = errors[0];

        var status = firstError.Type switch
        {
            ErrorType.NotFound => StatusCodes.Status404NotFound,
            ErrorType.Validation => StatusCodes.Status400BadRequest,
            ErrorType.Conflict => StatusCodes.Status409Conflict,
            ErrorType.Unauthorized => StatusCodes.Status401Unauthorized,
            ErrorType.Forbidden => StatusCodes.Status403Forbidden,
            _ => StatusCodes.Status500InternalServerError,
        };

        return Results.Json(new { Error = firstError.Code, Message = firstError.Description }, statusCode: status);
    }
}
