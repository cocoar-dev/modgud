using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ErrorOr;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Modgud.Api.Features.Admin.Provisioning;

/// <summary>
/// The two-step gate in front of a destructive manifest apply.
///
/// <para>The admin UI has always shown a plan before an apply, with deletions in red, and
/// refuses to apply while the plan carries errors. The raw API had none of that: one
/// <c>POST …/apply?prune=true</c> deleted everything the file did not mention, and nobody
/// had to look at the list first. A script could empty a realm on a typo.</para>
///
/// <para>So a pruning apply now answers the first call with <c>409</c>, the plan, and a
/// CONFIRMATION TOKEN; the caller repeats the call with <c>?confirm=&lt;token&gt;</c> to go
/// ahead. A script that means it passes the token; a script that did not expect deletions
/// sees them in the body it just got back. This does not stop anyone from confirming
/// blindly — nothing can, any more than the UI can stop someone clicking through without
/// reading. It makes the information unavoidable, which is the part we control.</para>
///
/// <para>Only <c>?prune=true</c> is gated, because only prune deletes: without it the
/// applier never reaches <c>PruneAsync</c> at all, so an ordinary apply can add and change
/// but never remove an entity. Gating it too would cost every caller a round trip for a
/// danger that does not exist.</para>
///
/// <para>The token is stateless — a DataProtection payload, not a stored row — so it needs
/// no cleanup and works across nodes (ADR 0022) without a shared table. It binds four
/// things, and a mismatch in any of them refuses:</para>
/// <list type="bullet">
///   <item>the REALM, so a token for stage cannot confirm an apply to prod;</item>
///   <item>the MANIFEST and the prune flag, so nobody can plan a harmless file and then
///   confirm a different one;</item>
///   <item>the DELETION SET the caller was shown, re-computed at confirm time — if the
///   realm moved in between and something else would now be deleted, the token is stale
///   and the caller has to look again;</item>
///   <item>an EXPIRY, so a token cannot be kept around and replayed later.</item>
/// </list>
/// </summary>
public sealed class ManifestApplyConfirmation(
    RealmManifestPlanner planner,
    IDataProtectionProvider dataProtection,
    IOptions<JsonOptions> jsonOptions,
    TimeProvider clock)
{
    private const string ProtectorPurpose = "Modgud.ManifestApplyConfirmation.v1";
    private const string Version = "v1";

    /// <summary>Long enough to read a plan and repeat the call, short enough that a token
    /// cannot sit in a shell history and be replayed against a realm that has moved on.</summary>
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    /// <summary>What the first call answers with: the plan the caller has to look at, and
    /// the token that lets them proceed.</summary>
    public sealed record Required(RealmPlanResult Plan, string ConfirmationToken, int Deletions);

    /// <summary>
    /// Decides whether this apply may run. Returns null to proceed, or the plan plus a
    /// token when the caller has not yet confirmed a set of deletions.
    /// </summary>
    public async Task<ErrorOr<Required?>> CheckAsync(
        string slug, RealmManifest manifest, bool prune, string? confirm, CancellationToken ct)
    {
        // No prune, no deletions, nothing to confirm.
        if (!prune) return (Required?)null;

        var planned = await planner.PlanAsync(slug, manifest, prune, ct: ct);
        if (planned.IsError) return planned.Errors;
        var plan = planned.Value;

        var deletions = plan.Sections
            .SelectMany(s => s.Entries.Where(e => e.Action == "delete").Select(e => $"{s.Name}/{e.Key}"))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        // A prune that deletes nothing is not destructive — do not make the caller
        // round-trip for an empty list.
        if (deletions.Count == 0) return (Required?)null;

        var payload = Digest(slug, manifest, prune);
        var deleteDigest = Digest(string.Join('\n', deletions));

        if (confirm is { Length: > 0 })
        {
            var verdict = Verify(confirm, slug, payload, deleteDigest);
            if (verdict is not null) return verdict.Value;
            return (Required?)null;
        }

        return new Required(plan, Issue(slug, payload, deleteDigest), deletions.Count);
    }

    private string Issue(string slug, string payload, string deleteDigest)
    {
        var expires = clock.GetUtcNow().Add(Lifetime).ToUnixTimeSeconds();
        return dataProtection.CreateProtector(ProtectorPurpose)
            .Protect($"{Version}|{slug}|{payload}|{deleteDigest}|{expires}");
    }

    private Error? Verify(string token, string slug, string payload, string deleteDigest)
    {
        string unprotected;
        try
        {
            unprotected = dataProtection.CreateProtector(ProtectorPurpose).Unprotect(token);
        }
        catch (CryptographicException)
        {
            return Error.Validation("Manifest.ConfirmationInvalid",
                "The confirmation token is not readable. Re-run the apply without ?confirm to get a fresh plan and token.");
        }

        var parts = unprotected.Split('|');
        if (parts.Length != 5 || parts[0] != Version)
            return Error.Validation("Manifest.ConfirmationInvalid", "The confirmation token has an unknown shape.");
        if (!long.TryParse(parts[4], out var expires) ||
            clock.GetUtcNow().ToUnixTimeSeconds() > expires)
            return Error.Validation("Manifest.ConfirmationExpired",
                "The confirmation token has expired. Re-run the apply without ?confirm to see the current plan.");
        if (!FixedTimeEquals(parts[1], slug))
            return Error.Validation("Manifest.ConfirmationRealmMismatch",
                $"The confirmation token was issued for a different realm, not '{slug}'.");
        if (!FixedTimeEquals(parts[2], payload))
            return Error.Validation("Manifest.ConfirmationPayloadMismatch",
                "The manifest changed since the plan was shown. Re-run the apply without ?confirm to review the new plan.");
        if (!FixedTimeEquals(parts[3], deleteDigest))
            return Error.Validation("Manifest.ConfirmationStale",
                "The realm changed since the plan was shown, and a different set of entities would now be deleted. Re-run the apply without ?confirm to see what.");
        return null;
    }

    /// <summary>Where a reviewer goes to look at a parked apply: the realm's own draft
    /// workspace, on the realm's own public origin — the control plane parks into a realm
    /// it is not hosted on, so a relative path would point at the wrong place.</summary>
    public static string? ReviewUrl(Modgud.Domain.Realms.Realm? realm, Guid draftId)
        => realm is null
            ? null
            : $"{Modgud.Authentication.RealmPublicUrl.RealmPublicBaseUrl(realm)}/admin/realm-config?draft={draftId}";

    /// <summary>What a parked apply hands back to the caller.</summary>
    public readonly record struct Parked(Guid? DraftId, string? ReviewUrl);

    /// <summary>
    /// Parks a pending apply as a shared draft in the TARGET realm. Drafts live in the
    /// realm's own database, so the control-plane route has to hop tenants exactly as the
    /// applier does — a fresh DI scope inside <c>TenantContext.Enter</c>, because
    /// <c>TenantedSessionFactory</c> binds a session to the ambient tenant.
    ///
    /// <para>Best effort: if parking fails, the caller still has the plan and the token in
    /// the response, so the confirm path works regardless. Losing the review link is worth
    /// less than losing the answer.</para>
    /// </summary>
    public static async Task<Parked> ParkAsync(
        IServiceScopeFactory scopeFactory, Modgud.Infrastructure.Realms.IRealmProvisioningService realms,
        string slug, RealmManifest manifest, string parkedBy, Guid userId, CancellationToken ct)
    {
        try
        {
            using var _ = Modgud.Infrastructure.Persistence.Tenancy.TenantContext.Enter(slug);
            using var scope = scopeFactory.CreateScope();
            var drafts = scope.ServiceProvider.GetRequiredService<RealmDraftService>();
            var parked = await drafts.ParkForReviewAsync(manifest, slug, parkedBy, userId, parkedBy, ct);
            if (parked.IsError) return new Parked(null, null);
            return new Parked(parked.Value.Id, ReviewUrl(await realms.GetRealmBySlugAsync(slug, ct), parked.Value.Id));
        }
        catch (Exception)
        {
            return new Parked(null, null);
        }
    }

    private string Digest(string slug, RealmManifest manifest, bool prune)
        => Digest($"{slug}\n{prune}\n{JsonSerializer.Serialize(manifest, jsonOptions.Value.SerializerOptions)}");

    private static string Digest(string value)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static bool FixedTimeEquals(string a, string b)
        => CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
