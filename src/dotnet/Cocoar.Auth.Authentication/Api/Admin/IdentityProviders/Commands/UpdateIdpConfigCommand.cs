using System.Text.Json;
using BuildingBlocks.Helper;
using ErrorOr;
using Marten;
using Cocoar.Auth.Authentication.Domain.ExternalAuth;
using Cocoar.Auth.Authentication.Domain.ExternalAuth.Events;
using Cocoar.Auth.Authentication.Identity.ExternalAuth;

namespace Cocoar.Auth.Authentication.Api.Admin.IdentityProviders.Commands;

/// <summary>
/// Admin saves the full edit form. Secret is handled in a separate rotation
/// command because (a) audit cleanliness — each rotation is its own event —
/// and (b) the frontend never re-submits an unchanged secret.
/// </summary>
public record UpdateIdpConfigCommand(
    Guid Id,
    string DisplayName,
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

public class UpdateIdpConfigHandler(
    IDocumentSession session,
    FlavorRegistry flavors,
    TimeProvider clock)
{
    public async Task<ErrorOr<IdpConfig>> Handle(UpdateIdpConfigCommand command, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(command.DisplayName))
            return Error.Validation("IdpConfig.DisplayNameRequired", "Display name is required.");

        var config = await session.LoadAsync<IdpConfig>(command.Id, ct);
        if (config is null || config.IsDeleted)
            return Error.NotFound("IdpConfig.NotFound", "IdP config not found.");

        if (!flavors.TryGet(config.Flavor, out var flavor))
            return Error.Validation("IdpConfig.UnknownFlavor",
                $"Flavor '{config.Flavor}' is no longer registered.");

        // Validate the flavor-specific payload shape (e.g. Entra needs TenantId).
        try { flavor.DeriveEndpoints(command.FlavorData); }
        catch (ArgumentException ex)
        {
            return Error.Validation("IdpConfig.FlavorDataInvalid", ex.Message);
        }

        // Display-name uniqueness across active configs.
        var nameTaken = await session.Query<IdpConfig>()
            .Where(c => !c.IsDeleted && c.Id != command.Id && c.DisplayName == command.DisplayName)
            .AnyAsync(ct);
        if (nameTaken)
            return Error.Conflict("IdpConfig.DisplayNameTaken",
                $"An IdP config named '{command.DisplayName}' already exists.");

        session.Events.Append(command.Id, new IdpConfigUpdatedEvent(
            Id: command.Id,
            DisplayName: command.DisplayName,
            ClientId: command.ClientId,
            Scopes: command.Scopes,
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
        return (await session.LoadAsync<IdpConfig>(command.Id, ct))!;
    }
}
