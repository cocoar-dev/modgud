using Microsoft.AspNetCore.Http;
using Modgud.Domain.Realms;
using Modgud.Infrastructure.RateLimiting;

namespace Modgud.Authentication.RateLimiting;

/// <summary>
/// ADR 0007 — the silent <c>source-registration</c> ceiling, consulted by the
/// registration pipeline right before it writes a pending record for an unknown
/// address. Outside an HTTP request (jobs, tests without a caller context) it allows.
/// </summary>
public interface IRegistrationThrottle
{
    Task<bool> AllowAsync(CancellationToken ct = default);
}

public sealed class RegistrationThrottle(
    IHttpContextAccessor accessor,
    IRateLimitEvaluator evaluator) : IRegistrationThrottle
{
    public async Task<bool> AllowAsync(CancellationToken ct = default)
    {
        var http = accessor.HttpContext;
        if (http is null) return true;
        var caller = AuthCallerContext.From(http);
        if (caller is null) return true;

        var policy = http.GetEndpoint()?.Metadata.GetMetadata<AuthRateLimitMetadata>()?.Policy
                     ?? AuthRateLimitPolicy.NativeOtp;
        var settings = await AuthRateLimitEndpointFilter.ResolveSettingsAsync(http, caller.ClientId, ct);
        return await evaluator.AllowRegistrationEntryAsync(policy, caller, settings, ct);
    }
}
