using System.Text.Json;
using ErrorOr;
using Marten;
using Cocoar.Auth.Authentication.Domain.LoginProviders;
using Cocoar.Auth.Authentication.Domain.LoginProviders.Events;
using Cocoar.Auth.Authentication.Identity.LoginProviders;

namespace Cocoar.Auth.Authentication.Api.Admin.LoginProviders.Commands;

/// <summary>
/// Admin creates a new login provider. Only core fields here — most settings
/// are applied via <c>UpdateLoginProviderCommand</c> (admin saves the full form
/// in one go). Secret rotation has its own command so audit trails stay clean.
/// <para>
/// <see cref="Type"/> is set on creation and immutable thereafter. For
/// <c>Internal</c> the flavor + flavor-data are ignored (no callbacks, no
/// secrets); for <c>Oidc</c> the flavor must be a registered
/// <see cref="ILoginProviderFlavor"/>. <c>Saml</c>/<c>Ldap</c>/<c>Kerberos</c>
/// are not yet wired and reject at command time.
/// </para>
/// </summary>
public record CreateLoginProviderCommand(
    string Flavor,
    string DisplayName,
    JsonDocument? FlavorData,
    LoginProviderType Type = LoginProviderType.Oidc,
    string? Description = null);

public class CreateLoginProviderHandler(
    IDocumentSession session,
    LoginProviderFlavorRegistry flavors,
    TimeProvider clock)
{
    public async Task<ErrorOr<LoginProvider>> Handle(CreateLoginProviderCommand command, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(command.DisplayName))
            return Error.Validation("LoginProvider.DisplayNameRequired", "Display name is required.");

        // Internal handling: no flavor lookup, no flavor-data validation.
        if (command.Type == LoginProviderType.Internal)
            return await CreateInternalAsync(command, ct);

        // Future-proof — block Saml/Ldap/Kerberos until a flavor surface lands.
        if (command.Type is LoginProviderType.Saml or LoginProviderType.Ldap or LoginProviderType.Kerberos)
            return Error.Validation(
                "LoginProvider.TypeNotSupported",
                $"Login provider type '{command.Type}' is not yet supported.");

        // Oidc-typed providers: full flavor validation.
        if (string.IsNullOrWhiteSpace(command.Flavor))
            return Error.Validation("LoginProvider.FlavorRequired", "Flavor is required for OIDC providers.");

        if (!flavors.TryGet(command.Flavor, out var flavor))
            return Error.Validation("LoginProvider.UnknownFlavor",
                $"Unknown flavor '{command.Flavor}'. Known flavors: {string.Join(", ", flavors.All.Select(f => f.Key))}.");

        // Let the flavor validate its own required fields.
        try { flavor.DeriveEndpoints(command.FlavorData); }
        catch (ArgumentException ex)
        {
            return Error.Validation("LoginProvider.FlavorDataInvalid", ex.Message);
        }

        var nameTaken = await session.Query<LoginProvider>()
            .Where(c => !c.IsDeleted && c.DisplayName == command.DisplayName)
            .AnyAsync(ct);
        if (nameTaken)
            return Error.Conflict("LoginProvider.DisplayNameTaken",
                $"A login provider named '{command.DisplayName}' already exists.");

        var id = Guid.NewGuid();
        var now = clock.GetUtcNow();

        var @event = new LoginProviderAddedEvent(
            Id: id,
            Type: LoginProviderType.Oidc,
            Flavor: flavor.Key,
            DisplayName: command.DisplayName,
            Description: command.Description,
            IsBuiltIn: false,
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

        session.Events.StartStream<LoginProvider>(id, @event);
        await session.SaveChangesAsync(ct);

        return (await session.LoadAsync<LoginProvider>(id, ct))!;
    }

    private async Task<ErrorOr<LoginProvider>> CreateInternalAsync(
        CreateLoginProviderCommand command, CancellationToken ct)
    {
        var nameTaken = await session.Query<LoginProvider>()
            .Where(c => !c.IsDeleted && c.DisplayName == command.DisplayName)
            .AnyAsync(ct);
        if (nameTaken)
            return Error.Conflict("LoginProvider.DisplayNameTaken",
                $"A login provider named '{command.DisplayName}' already exists.");

        var id = Guid.NewGuid();
        var now = clock.GetUtcNow();

        var @event = new LoginProviderAddedEvent(
            Id: id,
            Type: LoginProviderType.Internal,
            Flavor: LoginProviderFlavor.Internal,
            DisplayName: command.DisplayName,
            Description: command.Description,
            IsBuiltIn: false,
            Enabled: true, // Internal is enabled by default — there's no setup step.
            ClientId: string.Empty,
            ClientSecretEncrypted: null,
            Scopes: [],
            UserUpdateScript: string.Empty,
            StoreRawClaims: false,
            RawClaimsRetentionDays: null,
            AutoCreateUsers: false,
            AllowLinking: false,
            TrustForEmailLink: false,
            AllowedEmailDomains: null,
            IconName: null,
            ButtonColorHex: null,
            FlavorData: null,
            CreatedAt: now);

        session.Events.StartStream<LoginProvider>(id, @event);
        await session.SaveChangesAsync(ct);

        return (await session.LoadAsync<LoginProvider>(id, ct))!;
    }
}
