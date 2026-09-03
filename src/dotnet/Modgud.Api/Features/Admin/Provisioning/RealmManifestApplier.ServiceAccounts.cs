using BuildingBlocks.Helper;
using ErrorOr;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Features.ServiceAccounts;
using Modgud.Authorization.Events;
using Modgud.Authorization.Principals;
using Modgud.Infrastructure.OpenIddict;

namespace Modgud.Api.Features.Admin.Provisioning;

/// <summary>
/// Service-account section of the manifest applier — HULLS only (AccountName,
/// Purpose, IsActive, optional pinned Id). Credentials (client_credentials
/// OAuth clients + secrets) are deliberately NOT modelled: they are per-
/// environment secret material, issued via the service-account admin.
///
/// <para>Id pinning: a create honours the manifest's <c>Id</c> so a
/// stage → prod transfer keeps the SAME principal id — consuming applications
/// persist that id as their foreign key (change-feed contract). On update the
/// id is immutable and a differing manifest value is ignored (the planner
/// surfaces it as a note).</para>
///
/// <para>Upsert-only: service accounts are never pruned or staged-deleted —
/// deleting one kills live credentials, so that stays a deliberate live
/// operation in the SA admin. The planner mirrors this by never emitting
/// delete candidates for this section.</para>
/// </summary>
public sealed partial class RealmManifestApplier
{
    private static async Task ApplyServiceAccountsAsync(
        IServiceProvider sp, RealmManifest manifest, CancellationToken ct)
    {
        if (manifest.ServiceAccounts.Count == 0) return;

        var session = sp.GetRequiredService<IDocumentSession>();
        var revoker = sp.GetRequiredService<IOAuthGrantRevoker>();

        foreach (var sa in manifest.ServiceAccounts)
        {
            var ctx = $"service account '{sa.AccountName}'";
            var normalised = sa.AccountName.Trim().ToLowerInvariant();

            if (!ServiceAccountsEndpoints.AccountNamePattern.IsMatch(normalised))
                throw new ManifestApplyException(ctx, [Error.Validation(
                    "ServiceAccount.InvalidAccountName",
                    $"{ctx}: account name must be 2-64 chars, start with a letter or digit, and contain only lowercase letters, digits, dots, hyphens, or underscores.")]);

            var existing = await session.Query<ServiceAccount>()
                .FirstOrDefaultAsync(s => !s.IsDeleted && s.AccountName == normalised, ct);

            if (existing is null)
                await CreateServiceAccountAsync(session, sa, normalised, ctx, ct);
            else
                await UpdateServiceAccountAsync(session, revoker, existing, sa, ctx, ct);
        }
    }

    /// <summary>Mirror of V2_ServiceAccount_Create (hull path): same shared-namespace
    /// uniqueness checks, same created event — plus the pinned-id honouring.</summary>
    private static async Task CreateServiceAccountAsync(
        IDocumentSession session, RealmManifestServiceAccount sa, string normalised,
        string ctx, CancellationToken ct)
    {
        // Cross-Principal uniqueness — Person, ServiceAccount and Position share
        // the account-name namespace (any of them can end up as `sub`).
        if (await session.Query<Person>().AnyAsync(p => !p.IsDeleted && p.AccountName == normalised, ct)
            || await session.Query<PositionPrincipal>().AnyAsync(f => !f.IsDeleted && f.AccountName == normalised, ct))
            throw new ManifestApplyException(ctx, [Error.Conflict(
                "ServiceAccount.AccountNameTaken",
                $"{ctx}: account name '{normalised}' is already used by another principal.")]);

        // Shared pinned-id contract: a soft-deleted service account under this id is
        // revived (under the manifest's account name, so a rename before the delete
        // resolves too); a live entity is a conflict.
        var pinned = await ResolvePinnedAsync<ServiceAccount>(
            session, sa.Id, "ServiceAccount", ctx, x => x.IsDeleted, ct);

        var created = new ServiceAccount
        {
            Id = pinned.Id ?? Guid.NewGuid(),
            AccountName = normalised,
            Purpose = NormalisedPurpose(sa.Purpose.HasValue ? sa.Purpose.Value : null),
            IsActive = sa.IsActive ?? true,
        };
        var createdEvent = new ServiceAccountCreatedEvent(
            created.Id, created.AccountName, created.Purpose, created.IsActive);
        if (pinned.Revive)
            session.Events.Append(created.Id, createdEvent);
        else
            session.Events.StartStream<ServiceAccount>(created.Id, createdEvent);
        await session.SaveChangesAsync(ct);
    }

    /// <summary>Mirror of V2_ServiceAccount_Update (minus rename — AccountName is the
    /// manifest's natural key): v2 merge-patch on Purpose/IsActive, same
    /// active→inactive revocation cascade (deferred inside an apply).</summary>
    private static async Task UpdateServiceAccountAsync(
        IDocumentSession session, IOAuthGrantRevoker revoker,
        ServiceAccount existing, RealmManifestServiceAccount sa, string ctx, CancellationToken ct)
    {
        var wasActive = existing.IsActive;

        if (sa.Purpose.HasValue)
            existing.Purpose = NormalisedPurpose(sa.Purpose.Value);
        if (sa.IsActive.HasValue)
            existing.IsActive = sa.IsActive.Value;

        session.Events.Append(existing.Id, new ServiceAccountUpdatedEvent(
            existing.Id, existing.AccountName, existing.Purpose, existing.IsActive));
        await session.SaveChangesAsync(ct);

        // Audit #6 — deactivation cuts off live M2M access (sub = sa.Id across
        // every credential). Gate on the persisted transition like the endpoint.
        if (wasActive)
        {
            var persisted = await session.LoadAsync<ServiceAccount>(existing.Id, ct);
            if (persisted is { IsActive: false })
            {
                var subject = persisted.Id.ToString();
                await revoker.RevokeTokensBySubjectAsync(subject, ct);
                await revoker.RevokeAuthorizationsBySubjectAsync(subject, ct);
            }
        }
    }

    private static string? NormalisedPurpose(string? purpose)
        => string.IsNullOrWhiteSpace(purpose) ? null : purpose.Trim();
}
