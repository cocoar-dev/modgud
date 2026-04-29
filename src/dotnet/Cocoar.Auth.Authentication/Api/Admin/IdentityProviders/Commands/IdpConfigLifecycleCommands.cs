using ErrorOr;
using Marten;
using Cocoar.Auth.Authentication.Domain.ExternalAuth;
using Cocoar.Auth.Authentication.Domain.ExternalAuth.Events;
using Cocoar.Auth.Authentication.Identity.ExternalAuth;

namespace Cocoar.Auth.Authentication.Api.Admin.IdentityProviders.Commands;

// ── Enable ────────────────────────────────────────────────────────

public record EnableIdpConfigCommand(Guid Id);

public class EnableIdpConfigHandler(IDocumentSession session, TimeProvider clock)
{
    public async Task<ErrorOr<IdpConfig>> Handle(EnableIdpConfigCommand command, CancellationToken ct)
    {
        var config = await session.LoadAsync<IdpConfig>(command.Id, ct);
        if (config is null || config.IsDeleted)
            return Error.NotFound("IdpConfig.NotFound", "IdP config not found.");

        if (string.IsNullOrWhiteSpace(config.ClientId))
            return Error.Validation("IdpConfig.ClientIdRequired",
                "Cannot enable without a ClientId — set it via Update first.");
        if (config.ClientSecretEncrypted is null || config.ClientSecretEncrypted.Length == 0)
            return Error.Validation("IdpConfig.SecretRequired",
                "Cannot enable without a client secret — rotate it via Secret first.");

        if (!config.Enabled)
        {
            session.Events.Append(command.Id, new IdpConfigEnabledEvent(command.Id, clock.GetUtcNow()));
            await session.SaveChangesAsync(ct);
        }
        return (await session.LoadAsync<IdpConfig>(command.Id, ct))!;
    }
}

// ── Disable ───────────────────────────────────────────────────────

public record DisableIdpConfigCommand(Guid Id);

public class DisableIdpConfigHandler(IDocumentSession session, TimeProvider clock)
{
    public async Task<ErrorOr<IdpConfig>> Handle(DisableIdpConfigCommand command, CancellationToken ct)
    {
        var config = await session.LoadAsync<IdpConfig>(command.Id, ct);
        if (config is null || config.IsDeleted)
            return Error.NotFound("IdpConfig.NotFound", "IdP config not found.");
        if (config.Enabled)
        {
            session.Events.Append(command.Id, new IdpConfigDisabledEvent(command.Id, clock.GetUtcNow()));
            await session.SaveChangesAsync(ct);
        }
        return (await session.LoadAsync<IdpConfig>(command.Id, ct))!;
    }
}

// ── Delete (soft) ─────────────────────────────────────────────────

public record DeleteIdpConfigCommand(Guid Id);

public class DeleteIdpConfigHandler(IDocumentSession session, TimeProvider clock)
{
    public async Task<ErrorOr<Success>> Handle(DeleteIdpConfigCommand command, CancellationToken ct)
    {
        var config = await session.LoadAsync<IdpConfig>(command.Id, ct);
        if (config is null || config.IsDeleted)
            return Error.NotFound("IdpConfig.NotFound", "IdP config not found.");

        session.Events.Append(command.Id, new IdpConfigDeletedEvent(command.Id, clock.GetUtcNow()));
        await session.SaveChangesAsync(ct);
        return Result.Success;
    }
}

// ── Secret rotation ───────────────────────────────────────────────

public record RotateIdpConfigSecretCommand(Guid Id, string NewSecret, Guid? RotatedByUserId);

public class RotateIdpConfigSecretHandler(
    IDocumentSession session,
    IdpSecretStore secrets,
    TimeProvider clock)
{
    public async Task<ErrorOr<Success>> Handle(RotateIdpConfigSecretCommand command, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(command.NewSecret))
            return Error.Validation("IdpConfig.SecretEmpty", "Secret cannot be empty.");

        var config = await session.LoadAsync<IdpConfig>(command.Id, ct);
        if (config is null || config.IsDeleted)
            return Error.NotFound("IdpConfig.NotFound", "IdP config not found.");

        var encrypted = secrets.Encrypt(command.NewSecret);
        session.Events.Append(command.Id, new IdpConfigSecretRotatedEvent(
            Id: command.Id,
            ClientSecretEncrypted: encrypted,
            RotatedByUserId: command.RotatedByUserId,
            RotatedAt: clock.GetUtcNow()));

        await session.SaveChangesAsync(ct);
        return Result.Success;
    }
}
