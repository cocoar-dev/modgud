using System.Security.Cryptography;
using System.Text;

namespace Cocoar.Auth.Api.Middleware;

/// <summary>
/// Static bearer-token gate for the Prometheus scrape endpoint. Service-auth,
/// not user-auth — does not create a User principal, so the user-auth pipeline
/// (cookies, 2FA-enforcement) is untouched.
///
/// <para>The token comes from <see cref="ObservabilitySettings.PrometheusSettings.BearerToken"/>.
/// Empty token disables the check (dev convenience). Production-boot validation
/// in <c>Program.cs</c> refuses to start with Prometheus enabled but no token
/// set, so this branch never fires under release configuration with default
/// settings.</para>
///
/// <para>Mismatch returns <see cref="StatusCodes.Status404NotFound"/> — not 401 —
/// so the endpoint's existence stays unconfirmed to anonymous scanners.</para>
///
/// <para>Comparison uses <see cref="CryptographicOperations.FixedTimeEquals"/>
/// to avoid leaking the token byte-by-byte via response timing. Length mismatch
/// short-circuits before the compare; that's an acceptable trade-off (a content
/// equal-length probe converges only on tokens of the right length anyway).</para>
/// </summary>
public sealed class PrometheusBearerTokenMiddleware
{
    private const string BearerPrefix = "Bearer ";

    private readonly RequestDelegate _next;
    private readonly byte[] _expectedTokenUtf8;

    public PrometheusBearerTokenMiddleware(RequestDelegate next, ObservabilitySettings settings)
    {
        _next = next;
        _expectedTokenUtf8 = Encoding.UTF8.GetBytes(settings.Prometheus.BearerToken ?? string.Empty);
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (_expectedTokenUtf8.Length == 0)
        {
            // No token configured — caller opted into open dev-mode. Production
            // boot-validator catches this combination so the path doesn't run
            // unprotected under a release deployment.
            await _next(context);
            return;
        }

        var header = context.Request.Headers.Authorization.ToString();
        if (!header.StartsWith(BearerPrefix, StringComparison.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var providedTokenUtf8 = Encoding.UTF8.GetBytes(header[BearerPrefix.Length..]);
        if (providedTokenUtf8.Length != _expectedTokenUtf8.Length
            || !CryptographicOperations.FixedTimeEquals(providedTokenUtf8, _expectedTokenUtf8))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await _next(context);
    }
}
