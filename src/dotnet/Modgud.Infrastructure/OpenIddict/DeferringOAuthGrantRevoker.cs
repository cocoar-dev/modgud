using Microsoft.Extensions.DependencyInjection;
using Modgud.Infrastructure.Persistence.Tenancy;

namespace Modgud.Infrastructure.OpenIddict;

/// <summary>
/// Apply-scope-aware <see cref="IOAuthGrantRevoker"/> (ADR-0017 Phase 0): outside a
/// <see cref="TenantApplyTransaction"/> every call passes straight through to
/// <see cref="OpenIddictGrantRevoker"/> — byte-for-byte today's behavior. Inside one,
/// the revocation is a CONSEQUENCE of a staged config change and is deferred until
/// after the transaction committed; if the apply rolls back, it never runs.
///
/// <para>Deferred calls re-resolve the concrete revoker from a fresh scope, so they
/// operate on committed state with ordinary sessions. The revocation counts are not
/// knowable at defer time — deferred methods report 0; callers inside an apply
/// (the manifest applier, the equally-deferred kill switches) ignore the counts.</para>
/// </summary>
public sealed class DeferringOAuthGrantRevoker(OpenIddictGrantRevoker inner) : IOAuthGrantRevoker
{
    public Task<int> RevokeTokensBySubjectAsync(string subject, CancellationToken ct = default)
        => DeferOrRun($"revoke tokens for subject '{subject}'", ct,
            (r, c) => r.RevokeTokensBySubjectAsync(subject, c));

    public Task<int> RevokeAuthorizationsBySubjectAsync(string subject, CancellationToken ct = default)
        => DeferOrRun($"revoke authorizations for subject '{subject}'", ct,
            (r, c) => r.RevokeAuthorizationsBySubjectAsync(subject, c));

    public Task<int> RevokeTokensByApplicationIdAsync(string applicationId, CancellationToken ct = default)
        => DeferOrRun($"revoke tokens for application '{applicationId}'", ct,
            (r, c) => r.RevokeTokensByApplicationIdAsync(applicationId, c));

    public Task<int> RevokeTokensByAuthorizationIdAsync(string authorizationId, CancellationToken ct = default)
        => DeferOrRun($"revoke tokens for authorization '{authorizationId}'", ct,
            (r, c) => r.RevokeTokensByAuthorizationIdAsync(authorizationId, c));

    public Task<bool> RevokeAuthorizationByIdAsync(string authorizationId, CancellationToken ct = default)
    {
        if (TenantApplyTransaction.Current is not { } apply)
            return inner.RevokeAuthorizationByIdAsync(authorizationId, ct);
        apply.Defer($"revoke authorization '{authorizationId}'",
            (sp, c) => sp.GetRequiredService<OpenIddictGrantRevoker>()
                .RevokeAuthorizationByIdAsync(authorizationId, c));
        return Task.FromResult(false);
    }

    private Task<int> DeferOrRun(
        string what, CancellationToken ct,
        Func<OpenIddictGrantRevoker, CancellationToken, Task<int>> call)
    {
        if (TenantApplyTransaction.Current is not { } apply)
            return call(inner, ct);
        apply.Defer(what, (sp, c) => call(sp.GetRequiredService<OpenIddictGrantRevoker>(), c));
        return Task.FromResult(0);
    }
}
