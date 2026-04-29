using System.Text.Json;
using ErrorOr;
using Marten;
using Cocoar.Auth.Authentication.Domain.ExternalAuth;
using Cocoar.Auth.Authentication.Domain.ExternalAuth.Events;
using Cocoar.Auth.Authentication.Identity.ExternalAuth;

namespace Cocoar.Auth.Authentication.Api.Admin.IdentityProviders.Commands;

/// <summary>
/// Admin creates a new IdP configuration. Only core fields here — most settings
/// are applied via <c>UpdateIdpConfigCommand</c> (admin saves the full form in
/// one go). Secret rotation has its own command so audit trails stay clean.
/// </summary>
public record CreateIdpConfigCommand(
    string Flavor,
    string DisplayName,
    JsonDocument? FlavorData);

public class CreateIdpConfigHandler(
    IDocumentSession session,
    FlavorRegistry flavors,
    TimeProvider clock)
{
    public async Task<ErrorOr<IdpConfig>> Handle(CreateIdpConfigCommand command, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(command.DisplayName))
            return Error.Validation("IdpConfig.DisplayNameRequired", "Display name is required.");

        if (string.IsNullOrWhiteSpace(command.Flavor))
            return Error.Validation("IdpConfig.FlavorRequired", "Flavor is required.");

        if (!flavors.TryGet(command.Flavor, out var flavor))
            return Error.Validation("IdpConfig.UnknownFlavor",
                $"Unknown flavor '{command.Flavor}'. Known flavors: {string.Join(", ", flavors.All.Select(f => f.Key))}.");

        // Let the flavor validate its own required fields.
        try { flavor.DeriveEndpoints(command.FlavorData); }
        catch (ArgumentException ex)
        {
            return Error.Validation("IdpConfig.FlavorDataInvalid", ex.Message);
        }

        var nameTaken = await session.Query<IdpConfig>()
            .Where(c => !c.IsDeleted && c.DisplayName == command.DisplayName)
            .AnyAsync(ct);
        if (nameTaken)
            return Error.Conflict("IdpConfig.DisplayNameTaken",
                $"An IdP config named '{command.DisplayName}' already exists.");

        var id = Guid.NewGuid();
        var now = clock.GetUtcNow();

        var @event = new IdpConfigAddedEvent(
            Id: id,
            Flavor: flavor.Key,
            DisplayName: command.DisplayName,
            Enabled: false,
            ClientId: string.Empty,
            ClientSecretEncrypted: null,
            Scopes: [.. flavor.DefaultScopes],
            UserUpdateScript: flavor.DefaultUserUpdateScript,
            StoreRawClaims: flavor.DefaultStoreRawClaims,
            RawClaimsRetentionDays: null,
            AutoCreateUsers: false,
            AllowLinking: true,
            TrustForEmailLink: false,
            AllowedEmailDomains: null,
            IconName: flavor.DefaultIconName,
            ButtonColorHex: null,
            FlavorData: command.FlavorData,
            CreatedAt: now);

        session.Events.StartStream<IdpConfig>(id, @event);
        await session.SaveChangesAsync(ct);

        return (await session.LoadAsync<IdpConfig>(id, ct))!;
    }
}
