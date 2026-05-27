using System.Text.Json;
using ErrorOr;
using Marten;
using Modgud.Authentication.Domain.LoginProviders;
using Modgud.Authentication.Domain.LoginProviders.Events;
using Modgud.Authentication.Identity.LoginProviders;
using Modgud.Authentication.Identity.LoginProviders.Saml;
using Modgud.Authorization.Membership;

namespace Modgud.Authentication.Api.Admin.LoginProviders.Commands;

/// <summary>
/// Admin saves the full edit form. Secret is handled in a separate rotation
/// command because (a) audit cleanliness — each rotation is its own event —
/// and (b) the frontend never re-submits an unchanged secret.
/// <para>
/// <c>Type</c>, <c>Flavor</c>, and <c>IsBuiltIn</c> are immutable; they are
/// not part of the update surface.
/// </para>
/// </summary>
public record UpdateLoginProviderCommand(
    Guid Id,
    string DisplayName,
    string? Description,
    string ClientId,
    List<string> Scopes,
    string UserUpdateScript,
    bool StoreRawClaims,
    int? RawClaimsRetentionDays,
    bool AutoCreateUsers,
    bool AllowLinking,
    bool TrustForEmailLink,
    List<string>? AllowedEmailDomains,
    string? IconName,
    string? ButtonColorHex,
    JsonDocument? FlavorData);

public class UpdateLoginProviderHandler(
    IDocumentSession session,
    LoginProviderFlavorRegistry oidcFlavors,
    SamlFlavorRegistry samlFlavors,
    TimeProvider clock)
{
    public async Task<ErrorOr<LoginProvider>> Handle(UpdateLoginProviderCommand command, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(command.DisplayName))
            return Error.Validation("LoginProvider.DisplayNameRequired", "Display name is required.");

        // Length + nesting-depth caps before TS-pipeline reaches Acornima.
        // UserUpdateScript runs every external login attempt — an
        // unbounded or deeply-nested script makes the TS compiler do
        // arbitrary-time work on every request, and a 500-deep ternary
        // would crash the host. See JsEval threat model.
        var scriptInputError = ScriptInputLimits.Validate(
            command.UserUpdateScript, "LoginProvider.UserUpdateScript");
        if (scriptInputError is not null) return scriptInputError.Value;

        var config = await session.LoadAsync<LoginProvider>(command.Id, ct);
        if (config is null || config.IsDeleted)
            return Error.NotFound("LoginProvider.NotFound", "Login provider not found.");

        // Built-in (seeded) providers are immutable. Today this is only the
        // Internal provider written by the realm seeder; the same gate applies
        // to anything future seeders mark IsBuiltIn=true. Frontend hides the
        // edit button for these; this is the backend defense.
        if (config.IsBuiltIn)
            return LoginProviderErrors.InternalNotEditable(config.DisplayName);

        // Per-type flavor validation. OIDC checks the OidcFlavorRegistry +
        // DeriveEndpoints; SAML checks the SamlFlavorRegistry (FlavorData
        // shape is validated lazily by the metadata-fetch path).
        // Internal-typed providers skip flavor validation entirely.
        if (config.Type == LoginProviderType.Oidc)
        {
            if (!oidcFlavors.TryGet(config.Flavor, out var flavor))
                return Error.Validation("LoginProvider.UnknownFlavor",
                    $"OIDC flavor '{config.Flavor}' is no longer registered.");

            try { flavor.DeriveEndpoints(command.FlavorData); }
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

            // SAML has no DeriveEndpoints equivalent at update-time —
            // the metadata-fetch + parse happens when the manager re-
            // registers after this Update event is replayed.
        }

        // Display-name uniqueness across active providers.
        var nameTaken = await session.Query<LoginProvider>()
            .Where(c => !c.IsDeleted && c.Id != command.Id && c.DisplayName == command.DisplayName)
            .AnyAsync(ct);
        if (nameTaken)
            return Error.Conflict("LoginProvider.DisplayNameTaken",
                $"A login provider named '{command.DisplayName}' already exists.");

        // Type-aware projection of the update payload. SAML providers don't
        // have OIDC scopes — even if the client submits some (stale UI,
        // scripted caller), force the persisted value back to empty so the
        // aggregate stays internally consistent and admin grids / exports
        // can't mis-classify a SAML provider as OIDC by counting Scopes.
        var scopesForType = config.Type == LoginProviderType.Saml ? [] : command.Scopes;
        var clientIdForType = config.Type == LoginProviderType.Saml ? string.Empty : command.ClientId;

        session.Events.Append(command.Id, new LoginProviderUpdatedEvent(
            Id: command.Id,
            DisplayName: command.DisplayName,
            Description: command.Description,
            ClientId: clientIdForType,
            Scopes: scopesForType,
            UserUpdateScript: command.UserUpdateScript,
            StoreRawClaims: command.StoreRawClaims,
            RawClaimsRetentionDays: command.RawClaimsRetentionDays,
            AutoCreateUsers: command.AutoCreateUsers,
            AllowLinking: command.AllowLinking,
            TrustForEmailLink: command.TrustForEmailLink,
            AllowedEmailDomains: command.AllowedEmailDomains,
            IconName: command.IconName,
            ButtonColorHex: command.ButtonColorHex,
            FlavorData: command.FlavorData,
            UpdatedAt: clock.GetUtcNow()));

        await session.SaveChangesAsync(ct);
        return (await session.LoadAsync<LoginProvider>(command.Id, ct))!;
    }
}
