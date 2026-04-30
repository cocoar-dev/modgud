using ErrorOr;
using Marten;
using Cocoar.Auth.Authentication.Domain.LoginProviders;
using Cocoar.Auth.Authentication.Domain.LoginProviders.Events;
using Cocoar.Auth.Authentication.Identity.LoginProviders;

namespace Cocoar.Auth.Authentication.Api.Admin.LoginProviders.Commands;

// ── Enable ────────────────────────────────────────────────────────

public record EnableLoginProviderCommand(Guid Id);

public class EnableLoginProviderHandler(IDocumentSession session, TimeProvider clock)
{
    public async Task<ErrorOr<LoginProvider>> Handle(EnableLoginProviderCommand command, CancellationToken ct)
    {
        var config = await session.LoadAsync<LoginProvider>(command.Id, ct);
        if (config is null || config.IsDeleted)
            return Error.NotFound("LoginProvider.NotFound", "Login provider not found.");

        // Internal-typed providers don't need ClientId/Secret. Everything else does.
        if (config.Type != LoginProviderType.Internal)
        {
            if (string.IsNullOrWhiteSpace(config.ClientId))
                return Error.Validation("LoginProvider.ClientIdRequired",
                    "Cannot enable without a ClientId — set it via Update first.");
            if (config.ClientSecretEncrypted is null || config.ClientSecretEncrypted.Length == 0)
                return Error.Validation("LoginProvider.SecretRequired",
                    "Cannot enable without a client secret — rotate it via Secret first.");
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
