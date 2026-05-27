using ErrorOr;
using Marten;
using Modgud.Authentication.Domain.LoginProviders;
using Modgud.Authentication.Domain.LoginProviders.Events;
using Modgud.Authentication.Identity.LoginProviders;

namespace Modgud.Authentication.Api.Admin.LoginProviders.Commands;

// ── Enable ────────────────────────────────────────────────────────

public record EnableLoginProviderCommand(Guid Id);

public class EnableLoginProviderHandler(IDocumentSession session, TimeProvider clock)
{
    public async Task<ErrorOr<LoginProvider>> Handle(EnableLoginProviderCommand command, CancellationToken ct)
    {
        var config = await session.LoadAsync<LoginProvider>(command.Id, ct);
        if (config is null || config.IsDeleted)
            return Error.NotFound("LoginProvider.NotFound", "Login provider not found.");

        // Per-type pre-flight checks:
        //  - Internal: no readiness gate (it's the seeded built-in path).
        //  - Oidc: must have ClientId + ClientSecret set — otherwise the
        //    AuthnRequest the OIDC handler builds is unusable.
        //  - Saml: must have either MetadataUrl or MetadataXml set, so the
        //    SAML manager has IdP signing certs to validate Response
        //    signatures against. SP cert is auto-generated on first use,
        //    so no readiness gate there.
        if (config.Type == LoginProviderType.Oidc)
        {
            if (string.IsNullOrWhiteSpace(config.ClientId))
                return Error.Validation("LoginProvider.ClientIdRequired",
                    "Cannot enable without a ClientId — set it via Update first.");
            if (config.ClientSecretEncrypted is null || config.ClientSecretEncrypted.Length == 0)
                return Error.Validation("LoginProvider.SecretRequired",
                    "Cannot enable without a client secret — rotate it via Secret first.");
        }
        else if (config.Type == LoginProviderType.Saml)
        {
            var samlData = Modgud.Authentication.Identity.LoginProviders.Saml.SamlFlavorData.FromJson(config.FlavorData);
            if (string.IsNullOrWhiteSpace(samlData.MetadataUrl)
                && string.IsNullOrWhiteSpace(samlData.MetadataXml))
            {
                return Error.Validation("LoginProvider.SamlMetadataRequired",
                    "Cannot enable a SAML provider without IdP metadata. Set either MetadataUrl or MetadataXml via Update first.");
            }
        }

        if (!config.Enabled)
        {
            session.Events.Append(command.Id, new LoginProviderEnabledEvent(command.Id, clock.GetUtcNow()));
            await session.SaveChangesAsync(ct);
        }
        return (await session.LoadAsync<LoginProvider>(command.Id, ct))!;
    }
}

// ── Disable ───────────────────────────────────────────────────────

public record DisableLoginProviderCommand(Guid Id);

public class DisableLoginProviderHandler(IDocumentSession session, TimeProvider clock)
{
    public async Task<ErrorOr<LoginProvider>> Handle(DisableLoginProviderCommand command, CancellationToken ct)
    {
        var config = await session.LoadAsync<LoginProvider>(command.Id, ct);
        if (config is null || config.IsDeleted)
            return Error.NotFound("LoginProvider.NotFound", "Login provider not found.");

        // Built-in providers are not toggleable from the admin surface. The
        // seeded Internal provider must remain enabled — disabling it would
        // strip every realm of password/passkey login.
        if (config.IsBuiltIn)
            return LoginProviderErrors.InternalNotEditable(config.DisplayName);

        if (config.Enabled)
        {
            session.Events.Append(command.Id, new LoginProviderDisabledEvent(command.Id, clock.GetUtcNow()));
            await session.SaveChangesAsync(ct);
        }
        return (await session.LoadAsync<LoginProvider>(command.Id, ct))!;
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
