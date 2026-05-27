using System.Text.Json;
using ErrorOr;
using Marten;
using Modgud.Authentication.Domain.LoginProviders;
using Modgud.Authentication.Domain.LoginProviders.Events;
using Modgud.Authentication.Identity.LoginProviders;
using Modgud.Authentication.Identity.LoginProviders.Saml;

namespace Modgud.Authentication.Api.Admin.LoginProviders.Commands;

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
    LoginProviderFlavorRegistry oidcFlavors,
    SamlFlavorRegistry samlFlavors,
    TimeProvider clock)
{
    public async Task<ErrorOr<LoginProvider>> Handle(CreateLoginProviderCommand command, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(command.DisplayName))
            return Error.Validation("LoginProvider.DisplayNameRequired", "Display name is required.");

        // Internal handling: no flavor lookup, no flavor-data validation.
        if (command.Type == LoginProviderType.Internal)
            return await CreateInternalAsync(command, ct);

        // Ldap / Kerberos are still not wired — block at command time with the
        // same error code the runtime paths use.
        if (command.Type is LoginProviderType.Ldap or LoginProviderType.Kerberos)
            return LoginProviderErrors.TypeNotSupported(command.Type);

        if (command.Type == LoginProviderType.Saml)
            return await CreateSamlAsync(command, ct);

        // Oidc-typed providers: full flavor validation.
        if (string.IsNullOrWhiteSpace(command.Flavor))
            return Error.Validation("LoginProvider.FlavorRequired", "Flavor is required for OIDC providers.");

        if (!oidcFlavors.TryGet(command.Flavor, out var flavor))
            return Error.Validation("LoginProvider.UnknownFlavor",
                $"Unknown OIDC flavor '{command.Flavor}'. Known flavors: {string.Join(", ", oidcFlavors.All.Select(f => f.Key))}.");

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

    private async Task<ErrorOr<LoginProvider>> CreateSamlAsync(
        CreateLoginProviderCommand command, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(command.Flavor))
            return Error.Validation("LoginProvider.FlavorRequired", "Flavor is required for SAML providers.");

        if (!samlFlavors.TryGet(command.Flavor, out var flavor))
            return Error.Validation("LoginProvider.UnknownFlavor",
                $"Unknown SAML flavor '{command.Flavor}'. Known flavors: {string.Join(", ", samlFlavors.All.Select(f => f.Key))}.");

        // Unlike OIDC, SAML config doesn't need an endpoint-derivation
        // step at create time — the IdP metadata gets fetched / pasted
        // post-create via UpdateLoginProviderCommand. So we just store the
        // skeleton with the flavor's seeded defaults (AttributeMap, AMR
        // mapping) and let admin fill in MetadataUrl / MetadataXml later.

        var nameTaken = await session.Query<LoginProvider>()
            .Where(c => !c.IsDeleted && c.DisplayName == command.DisplayName)
            .AnyAsync(ct);
        if (nameTaken)
            return Error.Conflict("LoginProvider.DisplayNameTaken",
                $"A login provider named '{command.DisplayName}' already exists.");

        var id = Guid.NewGuid();
        var now = clock.GetUtcNow();

        // Apply flavor defaults to whatever FlavorData the admin passed in
        // (typically null for a fresh create — the EntraID / ADFS preset
        // seeds AttributeMap etc.). Persist as JsonDocument.
        var seededFlavorData = flavor.ApplyDefaults(SamlFlavorData.FromJson(command.FlavorData));
        var flavorDataJson = seededFlavorData.ToJson();

        var @event = new LoginProviderAddedEvent(
            Id: id,
            Type: LoginProviderType.Saml,
            Flavor: flavor.Key,
            DisplayName: command.DisplayName,
            Description: command.Description,
            IsBuiltIn: false,
            Enabled: false, // SAML providers start disabled — admin enables
                            // after metadata + smoke-test.
            ClientId: string.Empty, // SAML has no ClientId; field stays empty.
            ClientSecretEncrypted: null,
            Scopes: [], // SAML has no scopes.
            UserUpdateScript: flavor.DefaultUserUpdateScript,
            StoreRawClaims: flavor.DefaultStoreRawClaims,
            RawClaimsRetentionDays: null,
            AutoCreateUsers: false,
            AllowLinking: true,
            TrustForEmailLink: false,
            AllowedEmailDomains: null,
            IconName: flavor.DefaultIconName,
            ButtonColorHex: null,
            FlavorData: flavorDataJson,
            CreatedAt: now);

        session.Events.StartStream<LoginProvider>(id, @event);
        await session.SaveChangesAsync(ct);

        return (await session.LoadAsync<LoginProvider>(id, ct))!;
    }

    private async Task<ErrorOr<LoginProvider>> CreateInternalAsync(
        CreateLoginProviderCommand command, CancellationToken ct)
    {
        // At most one Internal provider per realm — the seeder writes it on
        // realm creation. Admins should not be able to create another via the
        // public command surface; the (already existing) seed is the only one.
        var hasInternal = await session.Query<LoginProvider>()
            .Where(c => !c.IsDeleted && c.Type == LoginProviderType.Internal)
            .AnyAsync(ct);
        if (hasInternal)
            return LoginProviderErrors.InternalAlreadyExists();

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
