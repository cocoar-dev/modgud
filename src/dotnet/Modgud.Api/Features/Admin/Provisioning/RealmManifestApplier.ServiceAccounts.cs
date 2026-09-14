using BuildingBlocks.Helper;
using ErrorOr;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Features.ServiceAccounts;
using Modgud.Application.DTOs.OAuth;
using Modgud.Application.DTOs.ServiceAccount;
using Modgud.Application.Services;
using Modgud.Authorization.Apps;
using Modgud.Authorization.Events;
using Modgud.Authorization.Principals;
using Modgud.Domain.OAuth.Applications;
using Modgud.Domain.OAuth.Common;
using Modgud.Infrastructure.OpenIddict;

namespace Modgud.Api.Features.Admin.Provisioning;

/// <summary>
/// Service-account section of the manifest applier: the account (AccountName, Purpose,
/// IsActive, optional pinned Id) and its <c>Credentials</c> — each one the SHAPE of a
/// client_credentials client, never a secret. A credential the account lacks is issued
/// through the SA-scoped op with a fresh secret returned once in the apply result; one
/// whose Id names a live credential is updated. The list is the desired set: when it is
/// present, a credential the account has but the list does not is deleted (the plan shows
/// it in red); an absent list leaves the credentials alone.
///
/// <para>Id pinning: a create honours the manifest's <c>Id</c> so a
/// stage → prod transfer keeps the SAME principal id — consuming applications
/// persist that id as their foreign key (change-feed contract). On update the
/// id is immutable and a differing manifest value is ignored (the planner
/// surfaces it as a note).</para>
///
/// <para>The ACCOUNT is never pruned or staged-deleted — deleting one kills every
/// credential it owns, so that stays a deliberate action in the SA admin. The planner
/// mirrors this by never emitting delete candidates for this section.</para>
/// </summary>
public sealed partial class RealmManifestApplier
{
    private static async Task ApplyServiceAccountsAsync(
        IServiceProvider sp, RealmManifest manifest, ManifestIdentity identity,
        IReadOnlyDictionary<string, App> apps, Dictionary<string, string> secrets,
        ManifestReferenceSkips skips, CancellationToken ct)
    {
        if (manifest.ServiceAccounts.Count == 0) return;

        var session = sp.GetRequiredService<IDocumentSession>();
        var revoker = sp.GetRequiredService<IOAuthGrantRevoker>();
        var oauth = sp.GetRequiredService<OAuthAdminService>();

        foreach (var sa in manifest.ServiceAccounts)
        {
            var ctx = $"service account '{sa.AccountName}'";
            var normalised = sa.AccountName.Trim().ToLowerInvariant();

            if (!ServiceAccountsEndpoints.AccountNamePattern.IsMatch(normalised))
                throw new ManifestApplyException(ctx, [Error.Validation(
                    "ServiceAccount.InvalidAccountName",
                    $"{ctx}: account name must be 2-64 chars, start with a letter or digit, and contain only lowercase letters, digits, dots, hyphens, or underscores.")]);

            // ADR 0024: the Id names the account, and an id-matched entry renames it (its
            // credentials keep working — they authenticate on the principal id, not the
            // name). Without an id the entry creates, and a taken account name fails with
            // ServiceAccount.AccountNameTaken rather than adopting a stranger's principal,
            // whose id consuming applications already hold as a foreign key.
            var existing = await MatchByPinnedIdAsync<ServiceAccount>(session, sa.Id, x => x.IsDeleted, ct);

            Guid accountId;
            if (existing is null)
            {
                accountId = await CreateServiceAccountAsync(session, sa, normalised, ctx, ct);
            }
            else
            {
                accountId = existing.Id;
                await UpdateServiceAccountAsync(session, revoker, existing, sa, normalised, ctx, ct);
            }
            identity.Assign(sa.Id, accountId);
            // The ACCOUNT is never pruned (deleting it kills every credential it owns), but
            // recording it tells prune that the manifest speaks for this account — which is
            // what makes it safe to prune the account's credentials below.
            identity.Applied(ManifestIdentity.Sections.ServiceAccounts, accountId);

            await ApplyCredentialsAsync(session, oauth, identity, apps, secrets, skips, sa, accountId, ctx, ct);
        }
    }

    /// <summary>
    /// Upserts an account's machine credentials — each one a confidential OAuth client with
    /// the <c>client_credentials</c> grant, bound to the account, created through the SAME
    /// canonical op the service-account admin uses.
    ///
    /// <para>Identity is the Id, as everywhere (ADR 0024): an entry whose Id names a live
    /// credential updates it, one without creates — and a taken client_id then fails loudly
    /// rather than adopting some other client. A created credential's secret is minted by
    /// the server and handed back once, in the apply result's ClientSecrets, exactly as an
    /// ordinary confidential client's is; a manifest never carries one in.</para>
    /// </summary>
    private static async Task ApplyCredentialsAsync(
        IDocumentSession session, OAuthAdminService oauth, ManifestIdentity identity,
        IReadOnlyDictionary<string, App> apps,
        Dictionary<string, string> secrets, ManifestReferenceSkips skips,
        RealmManifestServiceAccount sa, Guid accountId, string accountCtx, CancellationToken ct)
    {
        if (sa.Credentials is null) return;

        // Every credential the list speaks for, by id — what is left over afterwards is
        // what the list does NOT want the account to have.
        var listed = new HashSet<Guid>();
        foreach (var cred in sa.Credentials)
        {
            var ctx = $"{accountCtx} credential '{cred.ClientId}'";
            var live = await MatchByPinnedIdAsync<OAuthApplicationState>(
                session, cred.Id, x => x.IsDeleted, ct);
            if (live is not null)
                EnsureRenameable(false, cred.ClientId, live.ClientId, "ClientId", ctx);

            var appIds = cred.Apps is null
                ? null
                : OrUnchangedWhenNothingResolved(
                    cred.Apps.Select(slug => ResolveAppId(apps, slug, ctx, skips)).OfType<string>().ToList(),
                    cred.Apps.Count, ctx, "app", skips);

            if (live is null)
            {
                // The SA-scoped issue op, not the ordinary client create: an SA-owned
                // client pins its grant type, its secret policy and its link to the
                // account, and /admin/oauth/clients refuses to mutate one at all. Going
                // around that would be exactly the "new write logic" this applier exists
                // to avoid.
                var issued = await oauth.IssueServiceAccountCredentialAsync(accountId,
                    new IssueServiceAccountCredentialDto
                    {
                        ClientId = cred.ClientId,
                        DisplayName = OrNull(cred.DisplayName),
                        Scopes = cred.Scopes ?? [],
                        AppIds = appIds ?? [],
                        Enabled = cred.Enabled ?? true,
                        AccessTokenLifetime = OrNull(cred.AccessTokenLifetime),
                        AccessTokenType = ParseOptionalEnum<AccessTokenType>(
                            cred.AccessTokenType, $"{ctx} accessTokenType") ?? AccessTokenType.Reference,
                    }, ct);
                EnsureOk(issued, ctx);
                secrets[cred.ClientId] = issued.Value.ClientSecret;
                RegisterApplied(identity, ManifestIdentity.Sections.Clients,
                    cred.Id, issued.Value.Credential.Id, ctx);
                if (ShortGuid.TryParse(issued.Value.Credential.Id, out Guid issuedId)) listed.Add(issuedId);
            }
            else
            {
                identity.Assign(cred.Id, live.Id);
                identity.Applied(ManifestIdentity.Sections.Clients, live.Id);
                listed.Add(live.Id);
                EnsureOk(await oauth.UpdateServiceAccountCredentialAsync(accountId, live.Id.ToString(),
                    new UpdateServiceAccountCredentialDto
                    {
                        // Optional passes through: absent = unchanged, explicit null clears.
                        DisplayName = cred.DisplayName,
                        Scopes = cred.Scopes,
                        AppIds = appIds,
                        AccessTokenLifetime = cred.AccessTokenLifetime,
                        Enabled = cred.Enabled,
                        AccessTokenType = ParseOptionalEnum<AccessTokenType>(
                            cred.AccessTokenType, $"{ctx} accessTokenType"),
                    }, ct), ctx);
            }
        }

        // The list is the desired set — like Members on a group or Grants on a position.
        // A credential the account has but the list does not is deleted through the
        // SA-scoped op, so the machine that authenticated with it stops at apply; the
        // plan listed it as a deletion beforehand, and a pruning apply asked first.
        foreach (var leftover in await session.Query<OAuthApplicationState>()
                     .Where(x => !x.IsDeleted && x.LinkedServiceAccountId == accountId)
                     .ToListAsync(ct))
        {
            if (listed.Contains(leftover.Id)) continue;
            EnsureOk(await oauth.DeleteServiceAccountCredentialAsync(accountId, leftover.Id.ToString(), ct),
                $"{accountCtx} credential '{leftover.ClientId}' (removed from the list)");
        }
    }

    /// <summary>Mirror of V2_ServiceAccount_Create (hull path): same shared-namespace
    /// uniqueness checks, same created event — plus the pinned-id honouring.</summary>
    private static async Task<Guid> CreateServiceAccountAsync(
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
            session, ManifestHandle.AsPinnedId(sa.Id), "ServiceAccount", ctx, x => x.IsDeleted, ct);

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
        return created.Id;
    }

    /// <summary>Mirror of V2_ServiceAccount_Update: v2 merge-patch on Purpose/IsActive, the
    /// same active→inactive revocation cascade (deferred inside an apply), and — for an entry
    /// matched by its pinned Id — the same cross-principal-namespace-checked rename.</summary>
    private static async Task UpdateServiceAccountAsync(
        IDocumentSession session, IOAuthGrantRevoker revoker,
        ServiceAccount existing, RealmManifestServiceAccount sa, string normalised,
        string ctx, CancellationToken ct)
    {
        var wasActive = existing.IsActive;

        if (normalised != existing.AccountName)
        {
            if (await session.Query<Person>().AnyAsync(p => !p.IsDeleted && p.AccountName == normalised, ct)
                || await session.Query<PositionPrincipal>().AnyAsync(f => !f.IsDeleted && f.AccountName == normalised, ct)
                || await session.Query<ServiceAccount>().AnyAsync(
                    x => !x.IsDeleted && x.Id != existing.Id && x.AccountName == normalised, ct))
                throw new ManifestApplyException(ctx, [Error.Conflict(
                    "ServiceAccount.AccountNameTaken",
                    $"{ctx}: account name '{normalised}' is already used by another principal.")]);
            existing.AccountName = normalised;
        }

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
