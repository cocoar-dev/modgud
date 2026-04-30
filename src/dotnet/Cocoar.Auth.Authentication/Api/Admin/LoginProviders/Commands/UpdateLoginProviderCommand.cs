using System.Text.Json;
using ErrorOr;
using Marten;
using Cocoar.Auth.Authentication.Domain.LoginProviders;
using Cocoar.Auth.Authentication.Domain.LoginProviders.Events;
using Cocoar.Auth.Authentication.Identity.LoginProviders;

namespace Cocoar.Auth.Authentication.Api.Admin.LoginProviders.Commands;

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
    LoginProviderFlavorRegistry flavors,
    TimeProvider clock)
{
    public async Task<ErrorOr<LoginProvider>> Handle(UpdateLoginProviderCommand command, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(command.DisplayName))
            return Error.Validation("LoginProvider.DisplayNameRequired", "Display name is required.");

        var config = await session.LoadAsync<LoginProvider>(command.Id, ct);
        if (config is null || config.IsDeleted)
            return Error.NotFound("LoginProvider.NotFound", "Login provider not found.");

        // Internal-typed providers skip the OIDC-shaped validation entirely.
        if (config.Type != LoginProviderType.Internal)
        {
            if (!flavors.TryGet(config.Flavor, out var flavor))
                return Error.Validation("LoginProvider.UnknownFlavor",
                    $"Flavor '{config.Flavor}' is no longer registered.");

            // Validate the flavor-specific payload shape (e.g. Entra needs TenantId).
            try { flavor.DeriveEndpoints(command.FlavorData); }
            catch (ArgumentException ex)
            {
                return Error.Validation("LoginProvider.FlavorDataInvalid", ex.Message);
            }
        }

        // Display-name uniqueness across active providers.
        var nameTaken = await session.Query<LoginProvider>()
            .Where(c => !c.IsDeleted && c.Id != command.Id && c.DisplayName == command.DisplayName)
            .AnyAsync(ct);
        if (nameTaken)
            return Error.Conflict("LoginProvider.DisplayNameTaken",
                $"A login provider named '{command.DisplayName}' already exists.");

        session.Events.Append(command.Id, new LoginProviderUpdatedEvent(
            Id: command.Id,
            DisplayName: command.DisplayName,
            Description: command.Description,
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
        return (await session.LoadAsync<LoginProvider>(command.Id, ct))!;
    }
}
