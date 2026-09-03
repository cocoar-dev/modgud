using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Authentication.Applications;
using Modgud.Domain.Realms;
using Modgud.Infrastructure.Persistence.Tenancy;
using Modgud.Infrastructure.RateLimiting;

namespace Modgud.Authentication.RateLimiting;

/// <summary>Endpoint metadata: which auth rate-limit policy gates this endpoint.</summary>
public sealed class AuthRateLimitMetadata(AuthRateLimitPolicy policy)
{
    public AuthRateLimitPolicy Policy { get; } = policy;
}

/// <summary>Contract of a 429 (ADR 0007) — stable codes, machine-readable.</summary>
public sealed record RateLimitedResponse(string Error, string Policy, string Dimension, int RetryAfterSeconds)
{
    public const string ErrorCode = "rate_limited";
}

public static class AuthRateLimitEndpointExtensions
{
    /// <summary>
    /// Gate an endpoint with an auth rate-limit policy (replaces
    /// <c>RequireRateLimiting</c>). <paramref name="target"/> extracts the target
    /// identifier (mailbox / username) from the bound arguments so the target dimension
    /// can be charged; <paramref name="client"/> a claimed client id for unauthenticated
    /// public clients (the token endpoint).
    /// </summary>
    public static TBuilder RequireAuthRateLimit<TBuilder>(
        this TBuilder builder,
        AuthRateLimitPolicy policy,
        Func<EndpointFilterInvocationContext, string?>? target = null,
        Func<EndpointFilterInvocationContext, string?>? client = null)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.WithMetadata(new AuthRateLimitMetadata(policy));
        builder.AddEndpointFilter(new AuthRateLimitEndpointFilter(policy, target, client));
        return builder;
    }

    /// <summary>The first bound argument of type <typeparamref name="T"/>, if any.</summary>
    public static T? Argument<T>(this EndpointFilterInvocationContext ctx) where T : class =>
        ctx.Arguments.OfType<T>().FirstOrDefault();

    /// <summary>A form field of a POST (the token endpoint's <c>client_id</c>). The form
    /// is buffered by ASP.NET Core, so downstream readers get the cached collection.</summary>
    public static string? FormField(HttpContext http, string name)
    {
        if (!HttpMethods.IsPost(http.Request.Method) || !http.Request.HasFormContentType) return null;
        try
        {
            var form = http.Request.ReadFormAsync(http.RequestAborted).GetAwaiter().GetResult();
            return form.TryGetValue(name, out var value) ? value.ToString() : null;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// Runs the evaluator for the endpoint's policy. The caller context normally comes from
/// the middleware (Modgud.Api); the realm-independent installation branch has no
/// middleware, so the filter builds it itself when missing. On rejection: 429,
/// <c>Retry-After</c>, and a <see cref="RateLimitedResponse"/> body.
/// </summary>
public sealed class AuthRateLimitEndpointFilter(
    AuthRateLimitPolicy policy,
    Func<EndpointFilterInvocationContext, string?>? target,
    Func<EndpointFilterInvocationContext, string?>? client) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var http = ctx.HttpContext;
        var ct = http.RequestAborted;

        var caller = AuthCallerContext.From(http);
        if (caller is null)
        {
            var built = await http.RequestServices.GetRequiredService<IAuthCallerContextFactory>().BuildAsync(http, ct);
            if (built.IsError)
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: built.ErrorCode, detail: built.ErrorDetail);
            caller = built.Context!;
            http.Items[AuthCallerContext.ItemsKey] = caller;
        }

        var settings = await ResolveSettingsAsync(http, caller.ClientId, ct);
        var evaluator = http.RequestServices.GetRequiredService<IRateLimitEvaluator>();
        var decision = await evaluator.EvaluateAsync(policy, caller, settings, target?.Invoke(ctx), client?.Invoke(ctx), ct);

        if (decision.Outcome == RateLimitOutcome.Reject)
        {
            http.Response.Headers.RetryAfter = decision.RetryAfterSeconds.ToString();
            return Results.Json(new RateLimitedResponse(
                RateLimitedResponse.ErrorCode,
                AuthRateLimitDefaults.PolicyName(decision.Policy),
                RateLimitEvaluator.DimensionName(decision.Dimension ?? RateLimitDimension.Source),
                decision.RetryAfterSeconds), statusCode: StatusCodes.Status429TooManyRequests);
        }

        return await next(ctx);
    }

    /// <summary>The realm's (App-overridden) limits; null = shipped defaults — also for the
    /// realm-independent installation branch, which has no tenant.</summary>
    internal static async Task<AuthRateLimitSettings?> ResolveSettingsAsync(HttpContext http, string? clientId, CancellationToken ct)
    {
        if (http.Items[TenantConstants.HttpContextTenantIdKey] is not string { Length: > 0 }) return null;
        try
        {
            var resolver = http.RequestServices.GetRequiredService<IApplicationSettingsResolver>();
            return (await resolver.ResolveForRequestAsync(http, clientId, ct)).AuthRateLimits;
        }
        catch
        {
            // Never block an auth request on a settings-resolution hiccup: defaults apply.
            return null;
        }
    }
}
