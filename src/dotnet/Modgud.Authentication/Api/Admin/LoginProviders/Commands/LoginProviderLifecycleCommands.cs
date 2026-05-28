using System.Text.Json;
using ErrorOr;
using Marten;
using Modgud.Authentication.Domain.LoginProviders;
using Modgud.Authentication.Domain.LoginProviders.Events;
using Modgud.Authentication.Identity.LoginProviders;
using Modgud.Authentication.Identity.LoginProviders.Saml;

namespace Modgud.Authentication.Api.Admin.LoginProviders.Commands;

// ── Enable readiness gate ─────────────────────────────────────────
//
// Enable/Disable used to be standalone commands + HTTP endpoints. They are now
// folded into UpdateLoginProviderCommand (PATCH `Enabled`), so the grid can
// toggle via the same endpoint and "set metadata + enable" works in one save.
// The readiness gate lives here as a pure helper so the Update handler runs it
// against the POST-merge values.

public static class LoginProviderReadiness
{
    /// <summary>
    /// Pre-flight for flipping a provider to Enabled. Returns an error when the
    /// provider isn't ready, else null.
    ///  - Internal: no gate (seeded built-in path).
    ///  - OIDC: needs ClientId + a client secret, else the AuthnRequest is unusable.
    ///  - SAML: needs MetadataUrl or MetadataXml, so the manager has IdP signing
    ///    certs to validate against (SP cert is auto-generated on first use).
    /// </summary>
    public static Error? CheckCanEnable(LoginProviderType type, string clientId, bool hasSecret, JsonDocument? flavorData)
    {
        if (type == LoginProviderType.Oidc)
        {
            if (string.IsNullOrWhiteSpace(clientId))
                return Error.Validation("LoginProvider.ClientIdRequired",
                    "Cannot enable without a ClientId — set it first.");
            if (!hasSecret)
                return Error.Validation("LoginProvider.SecretRequired",
                    "Cannot enable without a client secret — rotate it first.");
        }
        else if (type == LoginProviderType.Saml)
        {
            var samlData = SamlFlavorData.FromJson(flavorData);
            if (string.IsNullOrWhiteSpace(samlData.MetadataUrl)
                && string.IsNullOrWhiteSpace(samlData.MetadataXml))
            {
                return Error.Validation("LoginProvider.SamlMetadataRequired",
                    "Cannot enable a SAML provider without IdP metadata. Set MetadataUrl or MetadataXml first.");
            }
        }

        return null;
    }
}

// ── Delete (soft) ─────────────────────────────────────────────────

public record DeleteLoginProviderCommand(Guid Id);

public class DeleteLoginProviderHandler(IDocumentSession session, TimeProvider clock)
{
    public async Task<ErrorOr<Success>> Handle(DeleteLoginProviderCommand command, CancellationToken ct)
    {
        var config = await session.LoadAsync<LoginProvider>(command.Id, ct);
        if (config is null || config.IsDeleted)
            return Error.NotFound("LoginProvider.NotFound", "Login provider not found.");

        if (config.IsBuiltIn)
            return Error.Validation("LoginProvider.CannotDeleteBuiltIn",
                $"Cannot delete the built-in login provider '{config.DisplayName}'.");

        session.Events.Append(command.Id, new LoginProviderDeletedEvent(command.Id, clock.GetUtcNow()));
        await session.SaveChangesAsync(ct);
        return Result.Success;
    }
}

// ── Secret rotation ───────────────────────────────────────────────

public record RotateLoginProviderSecretCommand(Guid Id, string NewSecret, Guid? RotatedByUserId);

public class RotateLoginProviderSecretHandler(
    IDocumentSession session,
    LoginProviderSecretStore secrets,
    TimeProvider clock)
{
    public async Task<ErrorOr<Success>> Handle(RotateLoginProviderSecretCommand command, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(command.NewSecret))
            return Error.Validation("LoginProvider.SecretEmpty", "Secret cannot be empty.");

        var config = await session.LoadAsync<LoginProvider>(command.Id, ct);
        if (config is null || config.IsDeleted)
            return Error.NotFound("LoginProvider.NotFound", "Login provider not found.");

        if (config.Type == LoginProviderType.Internal)
            return Error.Validation("LoginProvider.SecretNotApplicable",
                "Internal login providers do not have a client secret.");

        var encrypted = secrets.Encrypt(command.NewSecret);
        session.Events.Append(command.Id, new LoginProviderSecretRotatedEvent(
            Id: command.Id,
            ClientSecretEncrypted: encrypted,
            RotatedByUserId: command.RotatedByUserId,
            RotatedAt: clock.GetUtcNow()));

        await session.SaveChangesAsync(ct);
        return Result.Success;
    }
}
