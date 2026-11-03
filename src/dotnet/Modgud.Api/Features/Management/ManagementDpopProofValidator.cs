using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using Modgud.Infrastructure.OpenIddict.Dpop;
using Modgud.Infrastructure.Persistence.Tenancy;

namespace Modgud.Api.Features.Management;

/// <summary>Request-level PoP boundary for opted-in Management operations.
/// Record first use independently of the business session before invoking the
/// consumer. A later business failure consumes the proof; retries need a new one.</summary>
public sealed class ManagementDpopProofValidator(IServiceScopeFactory scopes, TimeProvider clock)
{
    public async Task<bool> ValidateAsync(HttpContext http, ClaimsPrincipal caller, AuthenticationHeaderValue authorization)
    {
        var confirmations = caller.FindAll(DpopConstants.ConfirmationClaim).ToList();
        var isDpop = authorization.Scheme.Equals("DPoP", StringComparison.OrdinalIgnoreCase);
        if (confirmations.Count == 0)
            return authorization.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase);
        if (!isDpop || confirmations.Count != 1) return false;

        string? expectedJkt;
        try
        {
            using var json = JsonDocument.Parse(confirmations[0].Value);
            if (json.RootElement.ValueKind != JsonValueKind.Object
                || json.RootElement.EnumerateObject().Count(property => property.NameEquals("jkt")) != 1
                || !json.RootElement.TryGetProperty("jkt", out var jkt) || jkt.ValueKind != JsonValueKind.String)
                return false;
            expectedJkt = jkt.GetString();
            if (string.IsNullOrWhiteSpace(expectedJkt)) return false;
        }
        catch (JsonException) { return false; }

        var header = http.Request.Headers[DpopConstants.HeaderName];
        if (header.Count != 1 || string.IsNullOrWhiteSpace(authorization.Parameter)) return false;
        var now = clock.GetUtcNow();
        DpopValidationResult proof;
        try
        {
            proof = DpopProofValidator.Validate(header.ToString(), http.Request.Method,
                $"{http.Request.Scheme}://{http.Request.Host}{http.Request.PathBase}{http.Request.Path}",
                now, accessToken: authorization.Parameter);
        }
        // The shared validator's signed out-of-range iat must fail closed here;
        // this does not change its other consumers or the SDK copy.
        catch (ArgumentOutOfRangeException) { return false; }
        if (!proof.IsValid || !string.Equals(proof.Jkt, expectedJkt, StringComparison.Ordinal)) return false;

        var ct = http.RequestAborted;
        ct.ThrowIfCancellationRequested();
        // A fresh scope must not silently enlist in an ambient manifest apply:
        // that would flush the replay claim without durably committing it.
        if (TenantApplyTransaction.Current is not null) return false;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var replay = scope.ServiceProvider.GetRequiredService<IDpopReplayStore>();
            var recorded = await replay.TryRecordAsync(proof.Jti!,
                proof.IssuedAt!.Value + DpopProofValidator.DefaultMaxAge + DpopProofValidator.DefaultClockSkew,
                now, ct);
            ct.ThrowIfCancellationRequested();
            return recorded;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return false; } // no operation on uncertain replay state
    }
}
