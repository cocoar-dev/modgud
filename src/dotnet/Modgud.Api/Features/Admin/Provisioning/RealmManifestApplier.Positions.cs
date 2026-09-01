using BuildingBlocks.Helper;
using ErrorOr;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Features.Positions;
using Modgud.Application.DTOs.Positions;
using Modgud.Application.Services;
using Modgud.Authorization.Events;
using Modgud.Authorization.Principals;
using Modgud.Domain.PositionTerminals;
using Modgud.Domain.ValueObjects;
using Modgud.Infrastructure.OpenIddict;
using Modgud.Infrastructure.PositionTerminals;

namespace Modgud.Api.Features.Admin.Provisioning;

/// <summary>
/// Position (MG-FT) section of the manifest applier. Positions have no application
/// service yet — their canonical write path lives in <see cref="PositionsEndpoints"/> —
/// so this partial shares the endpoint's validators (<see cref="PositionOpError"/>-based)
/// and appends the SAME domain events the endpoints append; only the thin orchestration
/// is duplicated here. When positions grow an application service, both call sites
/// should collapse onto it.
///
/// <para>Terminal SLOTS (device enrollments + their terminal-managed OAuth clients) are
/// deliberately NOT modelled: like service-account credentials they are one-time-secret
/// credential material bound to a device ceremony, not declarative config.</para>
/// </summary>
public sealed partial class RealmManifestApplier
{
    /// <summary>Grant/audit events need an actor; provisioning has no interactive user.
    /// Guid.Empty marks "the control plane" (same convention as system-issued writes).</summary>
    private static readonly Guid ProvisioningActor = Guid.Empty;

    /// <summary>
    /// Upserts every manifest position by AccountName (used by import AND apply — a fresh
    /// realm simply has no existing positions). Grants are user KEYS resolved like group
    /// members; a non-empty grant list replaces the live grant set, empty = no change.
    /// </summary>
    private static async Task ApplyPositionsAsync(
        IServiceProvider sp, RealmManifest manifest,
        IReadOnlyDictionary<string, Guid> userIds, CancellationToken ct)
    {
        if (manifest.Positions.Count == 0) return;

        // Mirrors the endpoints' feature gate: with the flag off the admin surface 404s,
        // so a manifest that declares positions must fail loudly instead of half-applying.
        if (!sp.GetRequiredService<AppSettings>().Features.PositionTerminals)
            throw new ManifestApplyException("positions", [Error.Validation("Manifest.FeatureDisabled",
                "The manifest declares Positions but the PositionTerminals feature is disabled on this deployment.")]);

        var session = sp.GetRequiredService<IDocumentSession>();
        var staffingRevoker = sp.GetRequiredService<IStaffingRevoker>();
        var revoker = sp.GetRequiredService<IOAuthGrantRevoker>();
        var now = DateTimeOffset.UtcNow;

        foreach (var pos in manifest.Positions)
        {
            var ctx = $"position '{pos.AccountName}'";
            var normalised = pos.AccountName.Trim().ToLowerInvariant();

            var grantUserIds = new List<Guid>(pos.Grants.Count);
            foreach (var key in pos.Grants)
                grantUserIds.Add(await ResolveUserRefAsync(session, userIds, key, $"{ctx} grant '{key}'", ct));

            var existing = await session.Query<PositionPrincipal>()
                .FirstOrDefaultAsync(p => !p.IsDeleted && p.AccountName == normalised, ct);

            if (existing is null)
                await CreatePositionAsync(session, pos, normalised, grantUserIds, now, ctx, ct);
            else
                await UpdatePositionAsync(session, staffingRevoker, revoker, existing, pos, grantUserIds, now, ctx, ct);
        }
    }

    /// <summary>Mirror of V2_Position_Create minus terminal-slot staging: same validators,
    /// same events, position + grant streams in ONE unit of work.</summary>
    private static async Task CreatePositionAsync(
        IDocumentSession session, RealmManifestPosition pos, string normalised,
        List<Guid> grantUserIds, DateTimeOffset now, string ctx, CancellationToken ct)
    {
        EnsureNoOpError(PositionsEndpoints.ValidateAccountName(normalised), ctx);
        EnsureNoOpError(await PositionsEndpoints.AccountNameTakenAsync(session, normalised, excludeId: null, ct), ctx);

        var policy = PositionsEndpoints.ApplyPolicy(PositionTerminalPolicy.Disabled, pos.TerminalPolicy, out var policyError);
        EnsureNoOpError(policyError, ctx);
        EnsureNoOpError(await PositionsEndpoints.ValidatePolicyAgainstRealmFloorAsync(session, policy, ct), ctx);

        foreach (var uid in grantUserIds)
            await EnsureGrantablePersonAsync(session, uid, ctx, ct);

        var fn = new PositionPrincipal
        {
            Id = Guid.NewGuid(),
            AccountName = normalised,
            Purpose = string.IsNullOrWhiteSpace(pos.Purpose) ? null : pos.Purpose.Trim(),
            IsActive = pos.IsActive ?? true,
            TerminalPolicy = policy,
        };
        session.Events.StartStream<PositionPrincipal>(fn.Id, new PositionPrincipalCreatedEvent(
            fn.Id, fn.AccountName, fn.Purpose, fn.IsActive, fn.TerminalPolicy));

        foreach (var uid in grantUserIds)
        {
            var grantId = Guid.NewGuid();
            session.Events.StartStream<PositionGrant>(grantId,
                new PositionGrantIssued(grantId, fn.Id, uid, ProvisioningActor, now));
        }

        await session.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Mirror of V2_Position_Update: merge + validate + full-replace event, then the SAME
    /// post-commit cascades (policy-tightening ends affected staffing sessions; an
    /// active→inactive transition revokes the position's tokens and ends its shifts).
    /// AccountName is the natural key, so a manifest can never rename a position.
    /// Declarative apply auto-confirms the policy consequences — the manifest IS the
    /// desired state (documented on the manifest field).
    /// </summary>
    private static async Task UpdatePositionAsync(
        IDocumentSession session, IStaffingRevoker staffingRevoker, IOAuthGrantRevoker revoker,
        PositionPrincipal existing, RealmManifestPosition pos, List<Guid> grantUserIds,
        DateTimeOffset now, string ctx, CancellationToken ct)
    {
        var wasActive = existing.IsActive;

        if (pos.Purpose is not null)
            existing.Purpose = string.IsNullOrWhiteSpace(pos.Purpose) ? null : pos.Purpose.Trim();
        if (pos.IsActive.HasValue)
            existing.IsActive = pos.IsActive.Value;

        // Same full-replace semantics as the PUT: the persisted policy is re-validated as
        // a write even when the manifest omits TerminalPolicy.
        var policy = PositionsEndpoints.ApplyPolicy(
            existing.TerminalPolicy, pos.TerminalPolicy ?? new PositionTerminalPolicyUpdateDto(), out var policyError);
        EnsureNoOpError(policyError, ctx);
        EnsureNoOpError(await PositionsEndpoints.ValidatePolicyAgainstRealmFloorAsync(session, policy, ct), ctx);

        var consequences = new PositionTerminalPolicyConsequencesDto();
        if (pos.TerminalPolicy is not null)
            consequences = await PositionsEndpoints.PreviewPolicyConsequencesAsync(
                session, existing.Id, existing.TerminalPolicy, policy, ct);

        existing.TerminalPolicy = policy;
        session.Events.Append(existing.Id, new PositionPrincipalUpdatedEvent(
            existing.Id, existing.AccountName, existing.Purpose, existing.IsActive, existing.TerminalPolicy));
        await session.SaveChangesAsync(ct);

        foreach (var encodedSessionId in consequences.StaffingSessionIds)
        {
            if (ShortGuid.TryDecode(encodedSessionId, out var staffingSessionId))
                await staffingRevoker.EndSessionAsync(
                    staffingSessionId, StaffingSessionEndReason.PolicyTightened, ct);
        }

        // Gate the revocation cascade on the PERSISTED active→inactive transition (same as
        // the PUT endpoint): the decision reads what the projection actually wrote, never
        // the manifest value directly.
        if (wasActive)
        {
            var persisted = await session.LoadAsync<PositionPrincipal>(existing.Id, ct);
            if (persisted is { IsActive: false })
            {
                await staffingRevoker.EndAllForPositionAsync(
                    persisted.Id, StaffingSessionEndReason.PositionDisabled, ct);
                var subject = persisted.Id.ToString();
                await revoker.RevokeTokensBySubjectAsync(subject, ct);
                await revoker.RevokeAuthorizationsBySubjectAsync(subject, ct);
            }
        }

        // ── Grants: desired-set reconciliation (non-empty replaces, empty = no change,
        //    matching the manifest's list semantics). Revoking ends the user's shifts —
        //    the same MG-FT-07 cascade the grants endpoint runs.
        if (grantUserIds.Count == 0) return;

        var live = await session.Query<PositionGrant>()
            .Where(g => g.PositionPrincipalId == existing.Id && g.Status != PositionGrantStatus.Revoked)
            .ToListAsync(ct);
        var desired = grantUserIds.ToHashSet();
        var held = live.Select(g => g.UserId).ToHashSet();

        var changed = false;
        foreach (var uid in desired.Where(u => !held.Contains(u)))
        {
            await EnsureGrantablePersonAsync(session, uid, ctx, ct);
            var grantId = Guid.NewGuid();
            session.Events.StartStream<PositionGrant>(grantId,
                new PositionGrantIssued(grantId, existing.Id, uid, ProvisioningActor, now));
            changed = true;
        }

        var toRevoke = live.Where(g => !desired.Contains(g.UserId)).ToList();
        foreach (var g in toRevoke)
        {
            session.Events.Append(g.Id, new PositionGrantRevoked(g.Id, ProvisioningActor, now));
            changed = true;
        }

        if (changed) await session.SaveChangesAsync(ct);

        foreach (var g in toRevoke)
            await staffingRevoker.EndAllForGrantAsync(g.Id, StaffingSessionEndReason.GrantRevoked, ct);
    }

    /// <summary>
    /// Prune counterpart — mirror of V2_Position_Delete: shared-terminal allow-lists lose
    /// the position (only a slot whose allow-list becomes empty dies with it, taking its
    /// terminal-managed client along), then soft-delete via the stream and run the same
    /// post-commit revocations. The live-notify bus publish of the endpoint is skipped —
    /// consumers resync via the resumable change feed after a provisioning prune.
    /// </summary>
    private static async Task PrunePositionsAsync(
        IServiceProvider sp, IDocumentSession session, OAuthAdminService oauth,
        RealmManifest manifest, bool prune, IReadOnlyDictionary<string, HashSet<string>>? targeted,
        CancellationToken ct)
    {
        // Feature dark → the realm cannot contain positions; nothing to prune.
        if (!sp.GetRequiredService<AppSettings>().Features.PositionTerminals) return;

        var keep = manifest.Positions
            .Select(p => p.AccountName.Trim().ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);
        // Targeted (staged) deletions restrict the sweep to their keys (lowercased
        // account names — normalized by the caller); full prune deletes everything.
        var targetedPositions = targeted?.GetValueOrDefault("positions");
        var staffingRevoker = sp.GetRequiredService<IStaffingRevoker>();
        var revoker = sp.GetRequiredService<IOAuthGrantRevoker>();
        var now = DateTimeOffset.UtcNow;

        foreach (var fn in await session.Query<PositionPrincipal>().Where(p => !p.IsDeleted).ToListAsync(ct))
        {
            if (keep.Contains(fn.AccountName)) continue;
            if (!prune && targetedPositions?.Contains(fn.AccountName) != true) continue;
            var ctx = $"prune position '{fn.AccountName}'";

            var slots = (await session.Query<TerminalEnrollment>().ToListAsync(ct))
                .Where(t => t.Status != TerminalEnrollmentStatus.Revoked &&
                            t.EffectiveAllowedPositionIds.Contains(fn.Id))
                .ToList();
            var revokedSlots = new List<TerminalEnrollment>();
            foreach (var slot in slots)
            {
                var remaining = slot.EffectiveAllowedPositionIds.Where(p => p != fn.Id).ToArray();
                if (remaining.Length == 0)
                {
                    session.Events.Append(slot.Id, new TerminalEnrollmentRevoked(slot.Id, ProvisioningActor, now));
                    if (await oauth.StageDeleteTerminalClientAsync(slot.OAuthApplicationId, ct) is { } slotError)
                        throw new ManifestApplyException(ctx, [slotError]);
                    revokedSlots.Add(slot);
                }
                else
                {
                    session.Events.Append(slot.Id, new TerminalAllowedPositionsChanged(
                        slot.Id, remaining, ProvisioningActor, now));
                }
            }

            session.Events.Append(fn.Id, new PositionPrincipalDeletedEvent(fn.Id));
            await session.SaveChangesAsync(ct);

            await staffingRevoker.EndAllForPositionAsync(
                fn.Id, StaffingSessionEndReason.PositionDisabled, ct);
            var subject = fn.Id.ToString();
            await revoker.RevokeTokensBySubjectAsync(subject, ct);
            await revoker.RevokeAuthorizationsBySubjectAsync(subject, ct);
            foreach (var slot in revokedSlots)
                await revoker.RevokeTokensByApplicationIdAsync(slot.OAuthApplicationId.ToString(), ct);
        }
    }

    /// <summary>Same grantability rules as the grants endpoint: the user must exist and be active.</summary>
    private static async Task EnsureGrantablePersonAsync(
        IDocumentSession session, Guid userId, string ctx, CancellationToken ct)
    {
        var person = await session.LoadAsync<Person>(userId, ct);
        if (person is null || person.IsDeleted)
            throw new ManifestApplyException(ctx,
                [Error.Validation("PositionGrant.UserNotFound", $"{ctx}: a grant user does not exist.")]);
        if (!person.IsActive)
            throw new ManifestApplyException(ctx,
                [Error.Validation("PositionGrant.UserInactive", $"{ctx}: an inactive user cannot receive a staffing grant.")]);
    }

    private static void EnsureNoOpError(PositionOpError? error, string what)
    {
        if (error is not null)
            throw new ManifestApplyException(what, [error.ToError()]);
    }
}
