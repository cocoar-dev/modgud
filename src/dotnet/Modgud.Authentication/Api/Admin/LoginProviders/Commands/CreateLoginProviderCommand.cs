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
/// Admin creates a new login provider. <see cref="Type"/> is set on creation
/// and immutable thereafter. Secret rotation has its own command so audit
/// trails stay clean — never set at Create.
/// <para>
/// All fields after <see cref="Description"/> are optional: when omitted, the
/// chosen flavor's defaults are used (legacy two-step flow). When the admin
/// submits the single-modal form, the full provider state arrives here in one
/// go and we skip the Update round-trip.
/// </para>
/// <para>
/// For <c>Internal</c> the flavor + flavor-data + all extended fields are
/// ignored (the Internal seed is fixed). For <c>Oidc</c> the flavor must be
/// a registered <see cref="ILoginProviderFlavor"/>. <c>Saml</c> validates
/// against <see cref="SamlFlavorRegistry"/>. <c>Ldap</c>/<c>Kerberos</c> are
/// not wired and reject at command time.
/// </para>
/// </summary>
public record CreateLoginProviderCommand(
    string Flavor,
    string DisplayName,
    string Slug,
    JsonDocument? FlavorData,
    LoginProviderType Type = LoginProviderType.Oidc,
    string? Description = null,
    bool? Enabled = null,
    string? ClientId = null,
    List<string>? Scopes = null,
    string? UserUpdateScript = null,
    bool? StoreRawClaims = null,
    int? RawClaimsRetentionDays = null,
    bool? AutoCreateUsers = null,
    bool? AllowLinking = null,
    bool? TrustForEmailLink = null,
    List<string>? AllowedEmailDomains = null,
    string? IconName = null,
    string? ButtonColorHex = null,
    bool? TrustForAuthorization = null,
    bool? AuthoritativeForProfile = null);

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

        // Slug is the URL-stable identifier (OIDC callback + SAML SP surface).
        // Validate format + per-realm uniqueness up front so every type path
        // below can trust it. Immutable after create — no Update equivalent.
        if (!LoginProviderSlugRules.IsValidFormat(command.Slug))
            return Error.Validation("LoginProvider.SlugInvalid",
                "Slug must be 3-64 chars, lowercase letters/digits/hyphens, start with a letter and end with a letter or digit.");

        var slugTaken = await session.Query<LoginProvider>()
            .Where(c => !c.IsDeleted && c.Slug == command.Slug)
            .AnyAsync(ct);
        if (slugTaken)
            return Error.Conflict("LoginProvider.SlugTaken",
                $"A login provider with slug '{command.Slug}' already exists in this realm.");

        // Same input-cap as Update — if the admin shipped a user-update script
        // at Create time, validate length + nesting before it enters storage.
        if (command.UserUpdateScript is not null)
        {
            var scriptInputError = ScriptInputLimits.Validate(
                command.UserUpdateScript, "LoginProvider.UserUpdateScript");
            if (scriptInputError is not null) return scriptInputError.Value;
        }

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

        // Readiness gate parity with EnableLoginProviderHandler: an OIDC
        // provider needs ClientId + ClientSecret before it can authenticate
        // anyone, and Create never carries a secret (RotateClientSecret is a
        // separate command for audit reasons). So Enabled=true at Create is
        // structurally unsafe — refuse it. The single-modal frontend already
        // hardcodes Enabled=false; this gate catches stale/scripted callers.
        if (command.Enabled == true)
            return Error.Validation("LoginProvider.SecretRequired",
                "Cannot create an OIDC provider as Enabled — set the client secret first via /secret, then enable explicitly.");

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
            Slug: command.Slug,
            DisplayName: command.DisplayName,
            Description: command.Description,
            IsBuiltIn: false,
            Enabled: command.Enabled ?? false,
            ClientId: command.ClientId ?? string.Empty,
            ClientSecretEncrypted: null,
            Scopes: command.Scopes ?? [.. flavor.DefaultScopes],
            UserUpdateScript: command.UserUpdateScript ?? flavor.DefaultUserUpdateScript,
            StoreRawClaims: command.StoreRawClaims ?? flavor.DefaultStoreRawClaims,
            RawClaimsRetentionDays: command.RawClaimsRetentionDays,
            AutoCreateUsers: command.AutoCreateUsers ?? false,
            AllowLinking: command.AllowLinking ?? true,
            TrustForEmailLink: command.TrustForEmailLink ?? false,
            TrustForAuthorization: command.TrustForAuthorization ?? false,
            AuthoritativeForProfile: command.AuthoritativeForProfile ?? false,
            AllowedEmailDomains: command.AllowedEmailDomains,
            IconName: command.IconName ?? flavor.DefaultIconName,
            ButtonColorHex: command.ButtonColorHex,
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

        // Readiness gate parity with EnableLoginProviderHandler: a SAML
        // provider needs IdP metadata (URL or pasted XML) before the scheme
        // manager can build a Saml2Configuration. If the admin asks for
        // Enabled=true at Create, the metadata MUST already be in FlavorData
        // — otherwise SamlSchemeBootstrap would register a half-broken scheme
        // and clicking the provider on the login page would land users on
        // /login?error=saml-no-metadata.
        if (command.Enabled == true)
        {
            var samlData = SamlFlavorData.FromJson(command.FlavorData);
            if (string.IsNullOrWhiteSpace(samlData.MetadataUrl)
                && string.IsNullOrWhiteSpace(samlData.MetadataXml))
            {
                return Error.Validation("LoginProvider.SamlMetadataRequired",
                    "Cannot create a SAML provider as Enabled without IdP metadata. Provide MetadataUrl or MetadataXml, or create disabled and enable explicitly after metadata is set.");
            }
        }

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
            Slug: command.Slug,
            DisplayName: command.DisplayName,
            Description: command.Description,
            IsBuiltIn: false,
            // SAML providers start disabled by default — admin enables
            // after metadata + smoke-test. Single-modal flow may opt in
            // via Enabled=true once the form is fully filled.
            Enabled: command.Enabled ?? false,
            ClientId: string.Empty, // SAML has no ClientId; field stays empty.
            ClientSecretEncrypted: null,
            Scopes: [], // SAML has no scopes.
            UserUpdateScript: command.UserUpdateScript ?? flavor.DefaultUserUpdateScript,
            StoreRawClaims: command.StoreRawClaims ?? flavor.DefaultStoreRawClaims,
            RawClaimsRetentionDays: command.RawClaimsRetentionDays,
            AutoCreateUsers: command.AutoCreateUsers ?? false,
            AllowLinking: command.AllowLinking ?? true,
            TrustForEmailLink: command.TrustForEmailLink ?? false,
            TrustForAuthorization: command.TrustForAuthorization ?? false,
            AuthoritativeForProfile: command.AuthoritativeForProfile ?? false,
            AllowedEmailDomains: command.AllowedEmailDomains,
            IconName: command.IconName ?? flavor.DefaultIconName,
            ButtonColorHex: command.ButtonColorHex,
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
            Slug: command.Slug,
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
            // Internal provider is strictly local — never trusted for external
            // authorization, never authoritative for profile.
            TrustForAuthorization: false,
            AuthoritativeForProfile: false,
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
