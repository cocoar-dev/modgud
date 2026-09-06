using Microsoft.Extensions.DependencyInjection;
using Modgud.Infrastructure.Persistence.Tenancy;

namespace Modgud.Authentication.Sessions;

/// <summary>
/// Apply-scope-aware <see cref="IUserAccessRevoker"/> (ADR-0017 Phase 0): outside a
/// <see cref="TenantApplyTransaction"/> every call passes straight through to
/// <see cref="UserAccessRevoker"/>. Inside one, the kill switch is a CONSEQUENCE of a
/// staged config change (user deactivation/prune) and is deferred until after the
/// transaction committed; on rollback it never runs.
///
/// <para>Ordering note: the interface asks callers to revoke BEFORE staging a
/// soft-delete so the security-stamp rotation can still load the user. Deferral
/// flips that order for applies — by the time the deferred revoke runs, a pruned
/// user's soft-delete is committed and the stamp rotation logs its "skipped"
/// warning. That is benign: a deleted user's cookies die at the next
/// SecurityStampValidator pass anyway because the user no longer loads, and the
/// token/session halves of the kill switch don't need the loadable user.</para>
/// </summary>
public sealed class DeferringUserAccessRevoker(UserAccessRevoker inner) : IUserAccessRevoker
{
    public Task RevokeAllAccessAsync(Guid userId, AccessRevocationReason reason, CancellationToken ct = default)
    {
        if (TenantApplyTransaction.Current is not { } apply)
            return inner.RevokeAllAccessAsync(userId, reason, ct);
        apply.Defer($"revoke all access for user '{userId}' ({reason})",
            (sp, c) => sp.GetRequiredService<UserAccessRevoker>().RevokeAllAccessAsync(userId, reason, c));
        return Task.CompletedTask;
    }
}
