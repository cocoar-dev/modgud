using System.Text.Json;
using ErrorOr;
using Marten;
using Modgud.Authentication.Domain.LoginProviders;
using Modgud.Authentication.Domain.LoginProviders.Events;
using Modgud.Authentication.Identity.LoginProviders;
using Modgud.Authentication.Identity.LoginProviders.Saml;
using Modgud.Authorization.Membership;
using Modgud.Domain.Common;

namespace Modgud.Authentication.Api.Admin.LoginProviders.Commands;

/// <summary>
/// PATCH the editable surface of a login provider. Every field is
/// <see cref="Optional{T}"/>: absent fields keep their current persisted value,
/// so the admin form can submit the full set while the grid submits only
/// <c>Enabled</c> to toggle. Secret is handled in a separate rotation command
/// (audit cleanliness; the frontend never re-submits an unchanged secret).
/// <para>
/// <c>Enabled</c> folds in the former Enable/Disable endpoints: a false→true
/// transition runs the readiness gate (against the POST-merge values, so
/// "set metadata + enable in one save" works) and emits the distinct
/// <c>LoginProviderEnabledEvent</c>; true→false emits the Disabled event.
/// </para>
/// <para><c>Type</c>, <c>Flavor</c>, and <c>IsBuiltIn</c> are immutable.</para>
/// </summary>
public record UpdateLoginProviderCommand(
    Guid Id,
    Optional<string> DisplayName,
    Optional<string?> Description,
    Optional<string> ClientId,
    Optional<List<string>> Scopes,
    Optional<string> UserUpdateScript,
    Optional<bool> StoreRawClaims,
    Optional<int?> RawClaimsRetentionDays,
    Optional<bool> AutoCreateUsers,
    Optional<bool> AllowLinking,
    Optional<bool> TrustForEmailLink,
    Optional<List<string>?> AllowedEmailDomains,
    Optional<string?> IconName,
    Optional<string?> ButtonColorHex,
    Optional<JsonDocument> FlavorData,
    Optional<bool> Enabled,
    // Federation v1. Defaulted to None so existing callers / tests are unchanged
    // and an omitted field preserves the persisted value (PATCH semantics).
    Optional<bool> TrustForAuthorization = default,
    Optional<bool> AuthoritativeForProfile = default);

public class UpdateLoginProviderHandler(
    IDocumentSession session,
    LoginProviderFlavorRegistry oidcFlavors,
    SamlFlavorRegistry samlFlavors,
    TimeProvider clock)
{
    public async Task<ErrorOr<LoginProvider>> Handle(UpdateLoginProviderCommand command, CancellationToken ct)
    {
        var config = await session.LoadAsync<LoginProvider>(command.Id, ct);
        if (config is null || config.IsDeleted)
            return Error.NotFound("LoginProvider.NotFound", "Login provider not found.");

        // Built-in (seeded) providers are immutable — and not toggleable, so
        // this also blocks disabling the Internal provider. Frontend hides the
        // controls; this is the backend defense.
        if (config.IsBuiltIn)
            return LoginProviderErrors.InternalNotEditable(config.DisplayName);

        // PATCH merge: provided fields win, absent fields keep their current
        // persisted value. The merged values are what we validate, persist, and
        // run the enable readiness gate against (so "set metadata + enable" in
        // one PATCH passes the gate on the freshly-set metadata).
        var displayName = command.DisplayName.OrDefault(config.DisplayName);
        var description = command.Description.HasValue ? command.Description.Value : config.Description;
        var clientId = command.ClientId.OrDefault(config.ClientId);
        var scopes = command.Scopes.HasValue ? command.Scopes.Value : config.Scopes;
        var userUpdateScript = command.UserUpdateScript.OrDefault(config.UserUpdateScript);
        var storeRawClaims = command.StoreRawClaims.OrDefault(config.StoreRawClaims);
        var rawRetention = command.RawClaimsRetentionDays.HasValue ? command.RawClaimsRetentionDays.Value : config.RawClaimsRetentionDays;
        var autoCreate = command.AutoCreateUsers.OrDefault(config.AutoCreateUsers);
        var allowLinking = command.AllowLinking.OrDefault(config.AllowLinking);
        var trustForEmailLink = command.TrustForEmailLink.OrDefault(config.TrustForEmailLink);
        var trustForAuthorization = command.TrustForAuthorization.OrDefault(config.TrustForAuthorization);
        var authoritativeForProfile = command.AuthoritativeForProfile.OrDefault(config.AuthoritativeForProfile);
        var allowedEmailDomains = command.AllowedEmailDomains.HasValue ? command.AllowedEmailDomains.Value : config.AllowedEmailDomains;
        var iconName = command.IconName.HasValue ? command.IconName.Value : config.IconName;
        var buttonColorHex = command.ButtonColorHex.HasValue ? command.ButtonColorHex.Value : config.ButtonColorHex;
        var flavorData = command.FlavorData.HasValue ? command.FlavorData.Value : config.FlavorData;

        if (string.IsNullOrWhiteSpace(displayName))
            return Error.Validation("LoginProvider.DisplayNameRequired", "Display name is required.");

        // Length + nesting-depth caps before the TS pipeline reaches Acornima —
        // only when the script is actually being changed (see JsEval threat model).
        if (command.UserUpdateScript.HasValue)
        {
            var scriptInputError = ScriptInputLimits.Validate(userUpdateScript, "LoginProvider.UserUpdateScript");
            if (scriptInputError is not null) return scriptInputError.Value;
        }

        // Per-type flavor validation against the merged FlavorData.
        if (config.Type == LoginProviderType.Oidc)
        {
            if (!oidcFlavors.TryGet(config.Flavor, out var flavor))
                return Error.Validation("LoginProvider.UnknownFlavor",
                    $"OIDC flavor '{config.Flavor}' is no longer registered.");

            try { flavor.DeriveEndpoints(flavorData); }
            catch (ArgumentException ex)
            {
                return Error.Validation("LoginProvider.FlavorDataInvalid", ex.Message);
            }
        }
        else if (config.Type == LoginProviderType.Saml)
        {
            if (!samlFlavors.TryGet(config.Flavor, out _))
                return Error.Validation("LoginProvider.UnknownFlavor",
                    $"SAML flavor '{config.Flavor}' is no longer registered.");
        }

        // Display-name uniqueness across active providers (merged name).
        var nameTaken = await session.Query<LoginProvider>()
            .Where(c => !c.IsDeleted && c.Id != command.Id && c.DisplayName == displayName)
            .AnyAsync(ct);
        if (nameTaken)
            return Error.Conflict("LoginProvider.DisplayNameTaken",
                $"A login provider named '{displayName}' already exists.");

        // SAML providers have no OIDC scopes / ClientId — force consistent so
        // grids/exports can't mis-classify by counting Scopes.
        var scopesForType = config.Type == LoginProviderType.Saml ? new List<string>() : scopes;
        var clientIdForType = config.Type == LoginProviderType.Saml ? string.Empty : clientId;

        var now = clock.GetUtcNow();

        // Emit an Updated event only when a non-Enabled field was provided. A
        // bare `Enabled` PATCH (the grid's inline toggle) yields just the
        // Enabled/Disabled audit event — no spurious "updated".
        var anyConfigFieldProvided =
            command.DisplayName.HasValue || command.Description.HasValue || command.ClientId.HasValue
            || command.Scopes.HasValue || command.UserUpdateScript.HasValue || command.StoreRawClaims.HasValue
            || command.RawClaimsRetentionDays.HasValue || command.AutoCreateUsers.HasValue
            || command.AllowLinking.HasValue || command.TrustForEmailLink.HasValue
            || command.TrustForAuthorization.HasValue || command.AuthoritativeForProfile.HasValue
            || command.AllowedEmailDomains.HasValue || command.IconName.HasValue
            || command.ButtonColorHex.HasValue || command.FlavorData.HasValue;

        if (anyConfigFieldProvided)
        {
            session.Events.Append(command.Id, new LoginProviderUpdatedEvent(
                Id: command.Id,
                DisplayName: displayName,
                Description: description,
                ClientId: clientIdForType,
                Scopes: scopesForType,
                UserUpdateScript: userUpdateScript,
                StoreRawClaims: storeRawClaims,
                RawClaimsRetentionDays: rawRetention,
                AutoCreateUsers: autoCreate,
                AllowLinking: allowLinking,
                TrustForEmailLink: trustForEmailLink,
                TrustForAuthorization: trustForAuthorization,
                AuthoritativeForProfile: authoritativeForProfile,
                AllowedEmailDomains: allowedEmailDomains,
                IconName: iconName,
                ButtonColorHex: buttonColorHex,
                FlavorData: flavorData,
                UpdatedAt: now));
        }

        // Enabled transition — folds in the former Enable/Disable endpoints.
        // The readiness gate runs against the MERGED values above, so enabling
        // while setting metadata in the same PATCH passes. Distinct audit
        // events keep the trail granular.
        if (command.Enabled.HasValue)
        {
            var desired = command.Enabled.Value;
            if (desired && !config.Enabled)
            {
                var hasSecret = config.ClientSecretEncrypted is { Length: > 0 };
                var gate = LoginProviderReadiness.CheckCanEnable(config.Type, clientIdForType, hasSecret, flavorData);
                if (gate is not null) return gate.Value;
                session.Events.Append(command.Id, new LoginProviderEnabledEvent(command.Id, now));
            }
            else if (!desired && config.Enabled)
            {
                session.Events.Append(command.Id, new LoginProviderDisabledEvent(command.Id, now));
            }
        }

        await session.SaveChangesAsync(ct);
        return (await session.LoadAsync<LoginProvider>(command.Id, ct))!;
    }
}
