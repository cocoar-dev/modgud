using Modgud.Authentication.RateLimiting;
using Modgud.Infrastructure.RateLimiting;

namespace Modgud.Api.Middleware;

/// <summary>
/// ADR 0007 — builds the <see cref="AuthCallerContext"/> for every endpoint that carries
/// an <see cref="AuthRateLimitMetadata"/>, after <c>RealmMiddleware</c> resolved the
/// tenant and before the endpoint's rate-limit filter runs. A malformed or untrusted
/// forwarded-address header is answered here with 400 so it never reaches the flow.
/// </summary>
public sealed class AuthCallerContextMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IAuthCallerContextFactory factory)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<AuthRateLimitMetadata>() is not null
            && AuthCallerContext.From(context) is null)
        {
            var built = await factory.BuildAsync(context, context.RequestAborted);
            if (built.IsError)
            {
                await Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: built.ErrorCode,
                    detail: built.ErrorDetail).ExecuteAsync(context);
                return;
            }
            context.Items[AuthCallerContext.ItemsKey] = built.Context;
        }

        await next(context);
    }
}
