using BuildingBlocks.Helper;
using ErrorOr;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Features.Positions;
using Modgud.Application.DTOs.Positions;
using Modgud.Application.Services;
using Modgud.Authorization.Apps;
using Modgud.Authorization.Events;
using Modgud.Authorization.Principals;
using Modgud.Domain.OAuth.Applications;
using Modgud.Domain.OAuth.Common;
using Modgud.Domain.PositionTerminals;
using Modgud.Domain.ValueObjects;
using Modgud.Infrastructure.OpenIddict;
using Modgud.Infrastructure.PositionTerminals;
using RealmSettingsDoc = Modgud.Domain.RealmSettings.RealmSettings;

namespace Modgud.Api.Features.Admin.Provisioning;

/// <summary>
/// Position (MG-FT) section of the manifest applier. Positions have no application
/// service yet — their canonical write path lives in <see cref="PositionsEndpoints"/> —
/// so this partial shares the endpoint's validators (<see cref="PositionOpError"/>-based)
/// and appends the SAME domain events the endpoints append; only the thin orchestration
/// is duplicated here. When positions grow an application service, both call sites
/// should collapse onto it.
///
/// <para>Terminal SLOTS travel as CONFIGURATION (name, location, RP ID, binding, the
/// positions they serve, the client's access profile), exactly like a service account's
/// credentials: a slot the position lacks is created with a fresh terminal-managed client
/// (a client-secret slot's secret returned once), one whose Id names a live slot is
/// updated. What never travels is the device ENROLLMENT — the DPoP key and the device-flow
/// approval are the ceremony, and a slot created by an apply is Pending until a device
/// enrolls. A manifest never removes a slot: revoking is terminal and stays an action.</para>
/// </summary>
public sealed partial class RealmManifestApplier
{
    /// <summary>Grant/audit events need an actor; provisioning has no interactive user.
    /// Guid.Empty marks "the control plane" (same convention as system-issued writes).</summary>
    private static readonly Guid ProvisioningActor = Guid.Empty;

    /// <summary>
    /// Upserts every manifest position by its <c>Id</c> (ADR 0024) — an entry without one
    /// creates, and a taken account name fails rather than adopting a stranger's position.
    /// Grants are user references resolved exactly like group members (id or
    /// <c>#handle</c>); a present grant list replaces the live grant set ([] revokes all),
    /// an absent list leaves the grants unchanged (v2 merge-patch).
    /// </summary>
    private static async Task ApplyPositionsAsync(
        IServiceProvider sp, RealmManifest manifest,
        ManifestIdentity identity, IReadOnlyDictionary<string, App> apps,
        Dictionary<string, string> secrets, ManifestReferenceSkips skips, CancellationToken ct)
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
        var appliedIds = new List<(RealmManifestPosition Position, Guid Id)>();

        foreach (var pos in manifest.Positions)
        {
            var ctx = $"position '{pos.AccountName}'";
            var normalised = pos.AccountName.Trim().ToLowerInvariant();

            List<Guid>? grantUserIds = null;
            if (pos.Grants is not null)
            {
                grantUserIds = new List<Guid>(pos.Grants.Count);
                foreach (var key in pos.Grants)
                {
                    if (await ResolveUserRefAsync(session, identity, key, $"{ctx} grant '{key}'", skips, ct) is { } uid)
                        grantUserIds.Add(uid);
                }
                // Revoking every grant is a real instruction ([]), resolving none of them is
                // not — leave the grant set alone rather than ending everyone's shifts.
                if (grantUserIds.Count == 0 && pos.Grants.Count > 0)
                {
                    skips.SkipWholeList(ctx, "grant", pos.Grants.Count);
                    grantUserIds = null;
                }
            }

            // ADR 0024: the Id names the position, so an id-matched entry renames it
            // instead of creating a second one.
            var existing = await MatchByPinnedIdAsync<PositionPrincipal>(session, pos.Id, x => x.IsDeleted, ct);

            Guid appliedId;
            if (existing is null)
            {
                appliedId = await CreatePositionAsync(session, pos, normalised, grantUserIds, now, ctx, ct);
            }
            else
            {
                appliedId = existing.Id;
                await UpdatePositionAsync(session, staffingRevoker, revoker, existing, pos, grantUserIds, now, ctx, ct);
            }
            identity.Assign(pos.Id, appliedId);
            identity.Applied(ManifestIdentity.Sections.Positions, appliedId);
            appliedIds.Add((pos, appliedId));
        }

        // Slots after EVERY position: a slot's AllowedPositions may name a position this
        // same manifest creates further down the list.
        var oauth = sp.GetRequiredService<OAuthAdminService>();
        foreach (var (pos, ownerId) in appliedIds)
        {
            if (pos.Terminals is null) continue;
            await ApplyTerminalsAsync(session, oauth, staffingRevoker, identity, apps, secrets, skips,
                pos, ownerId, $"position '{pos.AccountName}'", now, ct);
        }
    }

    /// <summary>
    /// Mirror of V2_PositionTerminals_Create / _Update / _SetPositions and
    /// V2_PositionTerminal_SetOAuthAccess, per slot: the same policy, binding, floor and
    /// allowed-position checks, the same events, the same post-commit cascades (ending the
    /// staffing sessions a removed position or a changed access profile invalidates).
    /// </summary>
    private static async Task ApplyTerminalsAsync(
        IDocumentSession session, OAuthAdminService oauth, IStaffingRevoker staffingRevoker,
        ManifestIdentity identity, IReadOnlyDictionary<string, App> apps, Dictionary<string, string> secrets,
        ManifestReferenceSkips skips, RealmManifestPosition pos, Guid ownerId, string posCtx,
        DateTimeOffset now, CancellationToken ct)
    {
        var owner = await session.LoadAsync<PositionPrincipal>(ownerId, ct)
                    ?? throw new ManifestApplyException(posCtx, [Error.Unexpected("Position.NotFound", $"{posCtx}: the position is not readable after its own apply.")]);
        var realm = await session.LoadAsync<RealmSettingsDoc>(RealmSettingsDoc.SingletonId, ct);
        var requiredBinding = realm?.PositionSecurity?.RequiredBindingCapabilities ?? BindingCapability.None;

        foreach (var slot in pos.Terminals!)
        {
            var ctx = $"{posCtx} terminal '{slot.DisplayName}'";
            var name = slot.DisplayName.Trim();
            if (name.Length == 0)
                throw new ManifestApplyException(ctx, [Error.Validation("Terminal.DisplayNameRequired", $"{ctx}: a display name is required.")]);

            // The served positions, by identity; the owner is always among them.
            List<Guid>? allowed = null;
            if (slot.AllowedPositions is not null)
            {
                allowed = [ownerId];
                foreach (var reference in slot.AllowedPositions)
                    if (await ResolvePositionRefAsync(session, identity, reference, $"{ctx} allowed position '{reference}'", skips, ct) is { } pid
                        && !allowed.Contains(pid))
                        allowed.Add(pid);
            }

            var live = await MatchByPinnedIdAsync<TerminalEnrollment>(
                session, slot.Id, x => x.Status == TerminalEnrollmentStatus.Revoked, ct);

            if (live is null)
            {
                if (!owner.TerminalPolicy.Enabled)
                    throw new ManifestApplyException(ctx, [Error.Validation("Terminal.TerminalPolicyDisabled",
                        $"{ctx}: enable terminal use on the position before declaring terminal slots.")]);
                var binding = string.IsNullOrWhiteSpace(slot.Binding) ? DeviceBindingIds.Dpop : slot.Binding;
                if (!PositionTerminalSecurity.TryGetWritableBinding(binding, out _))
                    throw new ManifestApplyException(ctx, [Error.Validation("Terminal.UnknownDeviceBinding", $"{ctx}: device binding '{binding}' is unknown or unavailable.")]);
                if (!owner.TerminalPolicy.AllowedDeviceBindings.Contains(binding, StringComparer.Ordinal))
                    throw new ManifestApplyException(ctx, [Error.Validation("Terminal.DeviceBindingNotAllowed", $"{ctx}: device binding '{binding}' is not allowed by the position policy.")]);
                if (!PositionTerminalSecurity.BindingMeetsFloor(binding, requiredBinding))
                    throw new ManifestApplyException(ctx, [Error.Validation("Terminal.DeviceBindingBelowRealmFloor", $"{ctx}: device binding '{binding}' does not meet the realm capability floor.")]);
                if (string.IsNullOrWhiteSpace(slot.WebAuthnRpId))
                    throw new ManifestApplyException(ctx, [Error.Validation("Terminal.RpIdRequired", $"{ctx}: WebAuthnRpId is required to create a slot.")]);
                foreach (var allowedId in allowed ?? [ownerId])
                    await EnsurePositionServableAsync(session, allowedId, binding, requiredBinding, ctx, ct);

                var access = await ResolveTerminalAccessAsync(oauth, apps, slot.Scopes ?? [], slot.Apps ?? [], ctx, skips, ct);

                // A free pinned id is honoured (ids stay identical across environments); a
                // REVOKED slot's id is not revived — revoke is terminal, the device needs a
                // fresh slot, so the manifest has to drop that Id.
                var pinned = await ResolvePinnedAsync<TerminalEnrollment>(
                    session, ManifestHandle.AsPinnedId(slot.Id), "Terminal", ctx,
                    x => x.Status == TerminalEnrollmentStatus.Revoked, ct);
                if (pinned.Revive)
                    throw new ManifestApplyException(ctx, [Error.Conflict("Terminal.Revoked",
                        $"{ctx}: the slot named by this Id was revoked; revoke is terminal — remove the Id to create a fresh slot.")]);
                var enrollmentId = pinned.Id ?? Guid.NewGuid();
                var applicationId = Guid.NewGuid();
                var clientId = $"terminal.{new ShortGuid(Guid.NewGuid()).ToString()[..8]}";
                if (oauth.StageCreateTerminalClient(applicationId, clientId, $"{owner.DisplayName} — {name}",
                        ownerId, enrollmentId, slot.WebAuthnRpId, binding, access, out var clientSecret) is { } clientError)
                    throw new ManifestApplyException(ctx, [clientError]);

                session.Events.StartStream<TerminalEnrollment>(enrollmentId, new TerminalEnrollmentCreated(
                    enrollmentId, ownerId, name,
                    slot.Location.HasValue && !string.IsNullOrWhiteSpace(slot.Location.Value) ? slot.Location.Value.Trim() : null,
                    applicationId, clientId, slot.WebAuthnRpId.Trim().ToLowerInvariant(),
                    ProvisioningActor, now, binding, (allowed ?? [ownerId]).ToArray()));
                await session.SaveChangesAsync(ct);
                if (clientSecret is not null) secrets[clientId] = clientSecret;
                identity.Assign(slot.Id, enrollmentId);
                continue;
            }

            // ── Update ───────────────────────────────────────────────────────────────
            if (live.PositionPrincipalId != ownerId)
                throw new ManifestApplyException(ctx, [Error.Conflict("Terminal.OwnedByAnotherPosition",
                    $"{ctx}: the slot named by this Id is owned by another position.")]);
            if (!string.IsNullOrWhiteSpace(slot.WebAuthnRpId)
                && !string.Equals(slot.WebAuthnRpId.Trim(), live.WebAuthnRpId, StringComparison.OrdinalIgnoreCase))
                throw new ManifestApplyException(ctx, [Error.Validation("Terminal.RpIdImmutable",
                    $"{ctx}: WebAuthnRpId is immutable (staff passkeys hang off it) — create a new slot for '{slot.WebAuthnRpId}'.")]);
            if (!string.IsNullOrWhiteSpace(slot.Binding) && !string.Equals(slot.Binding, live.Binding, StringComparison.Ordinal))
                throw new ManifestApplyException(ctx, [Error.Validation("Terminal.BindingImmutable",
                    $"{ctx}: the device binding is immutable after create — create a new slot for '{slot.Binding}'.")]);

            var location = !slot.Location.HasValue
                ? live.Location
                : string.IsNullOrWhiteSpace(slot.Location.Value) ? null : slot.Location.Value.Trim();
            if (name != live.DisplayName || location != live.Location)
                session.Events.Append(live.Id, new TerminalEnrollmentDetailsChanged(live.Id, name, location));

            var previous = live.EffectiveAllowedPositionIds.ToHashSet();
            var removedPositions = new List<Guid>();
            if (allowed is not null && !previous.SetEquals(allowed))
            {
                if (live.EnrollmentAuthorizationId is not null && allowed.Except(previous).Any())
                    throw new ManifestApplyException(ctx, [Error.Conflict("Terminal.ReenrollmentRequired",
                        $"{ctx}: adding a position to an enrolled terminal requires a fresh multi-position slot and enrollment.")]);
                foreach (var id in allowed)
                    await EnsurePositionServableAsync(session, id, live.Binding, requiredBinding, ctx, ct);
                session.Events.Append(live.Id, new TerminalAllowedPositionsChanged(live.Id, allowed.ToArray(), ProvisioningActor, now));
                removedPositions = previous.Except(allowed).ToList();
            }

            var accessChanged = false;
            if (slot.Scopes is not null || slot.Apps is not null)
            {
                var client = await session.LoadAsync<OAuthApplicationState>(live.OAuthApplicationId, ct);
                var currentScopes = client?.Permissions
                    .Where(x => x.StartsWith(OAuthPermissions.Prefixes.Scope, StringComparison.Ordinal))
                    .Select(x => x[OAuthPermissions.Prefixes.Scope.Length..]).ToList() ?? [];
                var currentAppIds = client?.AppIds.Select(ShortGuid.Encode).ToList() ?? [];
                var access = await ResolveTerminalAccessAsync(oauth, apps,
                    slot.Scopes ?? currentScopes, slot.Apps, ctx, skips, ct, currentAppIds);
                // The client's display name is not the manifest's to set — pass the current
                // one, otherwise the service reads null as "clear it".
                var change = await oauth.StageSetTerminalClientAccessAsync(
                    live.OAuthApplicationId, live.Id, client?.DisplayName, access, ct);
                EnsureOk(change, ctx);
                accessChanged = change.Value.AccessChanged;
            }

            await session.SaveChangesAsync(ct);
            identity.Assign(slot.Id, live.Id);

            // Post-commit cascades, as the endpoints run them.
            foreach (var removedPositionId in removedPositions)
            {
                var active = await session.Query<StaffingSession>()
                    .Where(s => s.TerminalEnrollmentId == live.Id && s.PositionPrincipalId == removedPositionId
                                && s.Status == StaffingSessionStatus.Active)
                    .ToListAsync(ct);
                foreach (var staffing in active)
                    await staffingRevoker.EndSessionAsync(staffing.Id, StaffingSessionEndReason.PolicyTightened, ct);
            }
            if (accessChanged)
                await staffingRevoker.EndAllForTerminalAsync(live.Id, StaffingSessionEndReason.PolicyTightened, ct);
        }
    }

    /// <summary>Same availability rule as the terminal endpoints: the position must be live,
    /// active, opted into terminal use, and allow the slot's binding above the realm floor.</summary>
    private static async Task EnsurePositionServableAsync(
        IDocumentSession session, Guid positionId, string binding, BindingCapability requiredBinding,
        string ctx, CancellationToken ct)
    {
        var position = await session.LoadAsync<PositionPrincipal>(positionId, ct);
        if (position is null || position.IsDeleted || !position.IsActive || !position.TerminalPolicy.Enabled
            || !position.TerminalPolicy.AllowedDeviceBindings.Contains(binding, StringComparer.Ordinal)
            || !PositionTerminalSecurity.BindingMeetsFloor(binding, requiredBinding))
            throw new ManifestApplyException(ctx, [Error.Validation("Terminal.PositionUnavailable",
                $"{ctx}: position '{ShortGuid.Encode(positionId)}' is not compatible with this terminal (inactive, terminal use off, or binding not allowed).")]);
    }

    /// <summary>The slot client's access profile through the same validator the endpoints
    /// use. Apps arrive as slugs; an unknown slug is skipped and reported, and a list that
    /// resolves to nothing keeps the client's current apps rather than clearing them.</summary>
    private static async Task<TerminalClientAccessConfiguration> ResolveTerminalAccessAsync(
        OAuthAdminService oauth, IReadOnlyDictionary<string, App> apps, IReadOnlyList<string> scopes,
        List<string>? appSlugs, string ctx, ManifestReferenceSkips skips, CancellationToken ct,
        List<string>? currentAppIds = null)
    {
        var appIds = appSlugs is null
            ? currentAppIds ?? []
            : OrUnchangedWhenNothingResolved(
                  appSlugs.Select(slug => ResolveAppId(apps, slug, ctx, skips)).OfType<string>().ToList(),
                  appSlugs.Count, ctx, "app", skips)
              ?? currentAppIds ?? [];
        var access = await oauth.ValidateTerminalClientAccessAsync(scopes, appIds, ct);
        EnsureOk(access, ctx);
        return access.Value;
    }

    /// <summary>A reference to a position, by identity (ADR 0024): a <c>#handle</c> from this
    /// manifest, or a real id the realm has; anything else is skipped and reported.</summary>
    private static async Task<Guid?> ResolvePositionRefAsync(
        IDocumentSession session, ManifestIdentity identity, ManifestRef reference,
        string context, ManifestReferenceSkips skips, CancellationToken ct)
    {
        if (reference.Handle is { } handle)
            return ResolveHandle(identity, handle, "position", context, skips);
        if (reference.ParsedId is not { } byId)
        {
            skips.Skip(context, "position", "carries no id or #handle, and a name is never resolved (ADR 0024)");
            return null;
        }
        if (identity.WasApplied(ManifestIdentity.Sections.Positions, byId)) return byId;
        var position = await session.LoadAsync<PositionPrincipal>(byId, ct);
        if (position is null || position.IsDeleted)
        {
            skips.Skip(context, $"position '{reference.Display}'", "no position with that id in this realm");
            return null;
        }
        VerifyHint(reference, context, "position", skips, position.AccountName);
        return position.Id;
    }

    /// <summary>Mirror of V2_Position_Create minus terminal-slot staging: same validators,
    /// same events, position + grant streams in ONE unit of work.</summary>
    private static async Task<Guid> CreatePositionAsync(
        IDocumentSession session, RealmManifestPosition pos, string normalised,
        List<Guid>? grantUserIds, DateTimeOffset now, string ctx, CancellationToken ct)
    {
        EnsureNoOpError(PositionsEndpoints.ValidateAccountName(normalised), ctx);
        EnsureNoOpError(await PositionsEndpoints.AccountNameTakenAsync(session, normalised, excludeId: null, ct), ctx);

        var policy = PositionsEndpoints.ApplyPolicy(PositionTerminalPolicy.Disabled, pos.TerminalPolicy, out var policyError);
        EnsureNoOpError(policyError, ctx);
        EnsureNoOpError(await PositionsEndpoints.ValidatePolicyAgainstRealmFloorAsync(session, policy, ct), ctx);

        foreach (var uid in grantUserIds ?? [])
            await EnsureGrantablePersonAsync(session, uid, ctx, ct);

        var pinned = await ResolvePinnedAsync<PositionPrincipal>(
            session, ManifestHandle.AsPinnedId(pos.Id), "Position", ctx, x => x.IsDeleted, ct);
        var purpose = OrNull(pos.Purpose);
        var fn = new PositionPrincipal
        {
            Id = pinned.Id ?? Guid.NewGuid(),
            AccountName = normalised,
            Purpose = string.IsNullOrWhiteSpace(purpose) ? null : purpose.Trim(),
            IsActive = pos.IsActive ?? true,
            TerminalPolicy = policy,
        };
        var createdEvent = new PositionPrincipalCreatedEvent(
            fn.Id, fn.AccountName, fn.Purpose, fn.IsActive, fn.TerminalPolicy);
        // Revive: append onto the soft-deleted stream instead of starting a new one.
        if (pinned.Revive)
            session.Events.Append(fn.Id, createdEvent);
        else
            session.Events.StartStream<PositionPrincipal>(fn.Id, createdEvent);

        foreach (var uid in grantUserIds ?? [])
        {
            var grantId = Guid.NewGuid();
            session.Events.StartStream<PositionGrant>(grantId,
                new PositionGrantIssued(grantId, fn.Id, uid, ProvisioningActor, now));
        }

        await session.SaveChangesAsync(ct);
        return fn.Id;
    }

    /// <summary>
    /// Mirror of V2_Position_Update: merge + validate + full-replace event, then the SAME
    /// post-commit cascades (policy-tightening ends affected staffing sessions; an
    /// active→inactive transition revokes the position's tokens and ends its shifts).
    /// An entry matched by its pinned Id RENAMES the position (same validation as the PUT:
    /// format + the shared principal account-name namespace).
    /// Declarative apply auto-confirms the policy consequences — the manifest IS the
    /// desired state (documented on the manifest field).
    /// </summary>
    private static async Task UpdatePositionAsync(
        IDocumentSession session, IStaffingRevoker staffingRevoker, IOAuthGrantRevoker revoker,
        PositionPrincipal existing, RealmManifestPosition pos, List<Guid>? grantUserIds,
        DateTimeOffset now, string ctx, CancellationToken ct)
    {
        var wasActive = existing.IsActive;

        var normalised = pos.AccountName.Trim().ToLowerInvariant();
        if (normalised != existing.AccountName)
        {
            EnsureNoOpError(PositionsEndpoints.ValidateAccountName(normalised), ctx);
            EnsureNoOpError(
                await PositionsEndpoints.AccountNameTakenAsync(session, normalised, existing.Id, ct), ctx);
            existing.AccountName = normalised;
        }

        if (pos.Purpose.HasValue)
            existing.Purpose = string.IsNullOrWhiteSpace(pos.Purpose.Value) ? null : pos.Purpose.Value.Trim();
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

        // ── Grants: desired-set reconciliation (a present list replaces — [] revokes
        //    everything; an absent list = no change, per the v2 merge-patch contract).
        //    Revoking ends the user's shifts — the same MG-FT-07 cascade the grants
        //    endpoint runs.
        if (grantUserIds is null) return;

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
        ManifestIdentity identity, bool prune, IReadOnlyDictionary<string, HashSet<string>>? targeted,
        CancellationToken ct)
    {
        // Feature dark → the realm cannot contain positions; nothing to prune.
        if (!sp.GetRequiredService<AppSettings>().Features.PositionTerminals) return;

        // Kept by identity (ADR 0024): the positions this apply just created or updated.
        // Targeted (staged) deletions restrict the sweep to their keys (lowercased
        // account names — normalized by the caller); full prune deletes everything.
        var targetedPositions = targeted?.GetValueOrDefault("positions");
        var staffingRevoker = sp.GetRequiredService<IStaffingRevoker>();
        var revoker = sp.GetRequiredService<IOAuthGrantRevoker>();
        var now = DateTimeOffset.UtcNow;

        foreach (var fn in await session.Query<PositionPrincipal>().Where(p => !p.IsDeleted).ToListAsync(ct))
        {
            if (identity.WasApplied(ManifestIdentity.Sections.Positions, fn.Id)) continue;
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
