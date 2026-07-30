using System.Text.Json;
using ErrorOr;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Authentication.Api.Admin.LoginProviders.Commands;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Authentication.Domain.LoginProviders;
using Modgud.Authentication.Domain.LoginProviders.Events;
using Modgud.Authentication.Identity.LoginProviders;
using Wolverine;

namespace Modgud.Api.Tests.ExternalAuth;

[Collection(IntegrationTestCollection.Name)]
public class LoginProviderTests : IntegrationTestBase
{
    public LoginProviderTests(SharedPostgresFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Create_PersistsLoginProvider_WithFlavorDefaults()
    {
        // LoginProvider can be created via Wolverine-style command handler,
        // the event is persisted, the inline projection materializes the
        // document with flavor-derived defaults.
        using var scope = Factory.Services.CreateScope();
        var bus = GetTenantedMessageBus(scope);

        var flavorData = JsonDocument.Parse("""{"TenantId": "11111111-2222-3333-4444-555555555555"}""");
        var command = new CreateLoginProviderCommand(
            Flavor: LoginProviderFlavor.EntraId,
            DisplayName: "Acme Entra",
            Slug: "acme-entra",
            FlavorData: flavorData);

        var result = await bus.InvokeAsync<ErrorOr<LoginProvider>>(command);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : "");
        var config = result.Value;

        Assert.NotEqual(Guid.Empty, config.Id);
        Assert.Equal(LoginProviderType.Oidc, config.Type);
        Assert.Equal(LoginProviderFlavor.EntraId, config.Flavor);
        Assert.Equal("Acme Entra", config.DisplayName);
        Assert.False(config.IsBuiltIn);
        Assert.False(config.Enabled, "New config should be disabled until admin opts in");
        Assert.True(config.StoreRawClaims, "Entra should default to storing raw claims");
        Assert.Contains("openid", config.Scopes);
        Assert.False(string.IsNullOrWhiteSpace(config.UserUpdateScript));
        Assert.False(config.AutoCreateUsers, "Auto-create defaults off");
        Assert.True(config.AllowLinking, "Linking allowed by default");
        Assert.False(config.TrustForEmailLink, "Trust-for-email defaults off (impersonation hardening)");
    }

    [Fact]
    public async Task Create_ReplaysFromEventStream()
    {
        using var scope = Factory.Services.CreateScope();
        var bus = GetTenantedMessageBus(scope);

        var flavorData = JsonDocument.Parse("""{"MetadataUri": "https://idp.test/.well-known/openid-configuration"}""");
        var result = await bus.InvokeAsync<ErrorOr<LoginProvider>>(new CreateLoginProviderCommand(
            Flavor: LoginProviderFlavor.GenericOidc,
            DisplayName: "Test OIDC",
            Slug: "test-oidc",
            FlavorData: flavorData));
        Assert.False(result.IsError);

        // Verify the event is in the stream and the projection can replay it.
        using var scope2 = Factory.Services.CreateScope();
        var session = scope2.ServiceProvider.GetRequiredService<IDocumentSession>();

        var events = await session.Events.FetchStreamAsync(result.Value.Id);
        Assert.Single(events);
        Assert.IsType<LoginProviderAddedEvent>(events[0].Data);

        var doc = await session.LoadAsync<LoginProvider>(result.Value.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(doc);
        Assert.Equal("Test OIDC", doc!.DisplayName);
    }

    [Fact]
    public async Task Create_DuplicateDisplayName_Conflicts()
    {
        using var scope = Factory.Services.CreateScope();
        var bus = GetTenantedMessageBus(scope);

        var flavorData = JsonDocument.Parse("""{"MetadataUri": "https://idp.test/.well-known/openid-configuration"}""");
        await bus.InvokeAsync<ErrorOr<LoginProvider>>(new CreateLoginProviderCommand(LoginProviderFlavor.GenericOidc, "Duplicate", "dup-one", flavorData));

        // Distinct slug so the slug-uniqueness gate passes and the duplicate
        // DisplayName is what trips the conflict.
        var second = await bus.InvokeAsync<ErrorOr<LoginProvider>>(new CreateLoginProviderCommand(LoginProviderFlavor.GenericOidc, "Duplicate", "dup-two", flavorData));
        Assert.True(second.IsError);
        Assert.Equal("LoginProvider.DisplayNameTaken", second.FirstError.Code);
    }

    [Fact]
    public async Task Create_UnknownFlavor_ValidationError()
    {
        using var scope = Factory.Services.CreateScope();
        var bus = GetTenantedMessageBus(scope);

        var result = await bus.InvokeAsync<ErrorOr<LoginProvider>>(
            new CreateLoginProviderCommand("NopeFlavor", "X", "nope-flavor", null));

        Assert.True(result.IsError);
        Assert.Equal("LoginProvider.UnknownFlavor", result.FirstError.Code);
    }

    [Fact]
    public async Task Create_MissingFlavorRequiredField_ValidationError()
    {
        using var scope = Factory.Services.CreateScope();
        var bus = GetTenantedMessageBus(scope);

        // EntraId flavor requires TenantId — passing null FlavorData must fail.
        var result = await bus.InvokeAsync<ErrorOr<LoginProvider>>(
            new CreateLoginProviderCommand(LoginProviderFlavor.EntraId, "NoTenant", "no-tenant", null));

        Assert.True(result.IsError);
        Assert.Equal("LoginProvider.FlavorDataInvalid", result.FirstError.Code);
    }

    [Fact]
    public async Task Create_InternalType_DoesNotRequireFlavorOrSecret()
    {
        // Phase 1 addition: Internal-typed providers skip the OIDC-shaped
        // validation entirely (no Flavor lookup, no FlavorData, no
        // ClientId/Secret). They land enabled, since there is no setup step.
        using var scope = Factory.Services.CreateScope();
        var bus = GetTenantedMessageBus(scope);

        var result = await bus.InvokeAsync<ErrorOr<LoginProvider>>(new CreateLoginProviderCommand(
            Flavor: string.Empty,
            DisplayName: "My Internal " + Guid.NewGuid().ToString("N")[..6],
            Slug: "my-internal",
            FlavorData: null,
            Type: LoginProviderType.Internal));

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : "");
        var config = result.Value;
        Assert.Equal(LoginProviderType.Internal, config.Type);
        Assert.Equal(LoginProviderFlavor.Internal, config.Flavor);
        Assert.Empty(config.ClientId);
        Assert.Null(config.ClientSecretEncrypted);
        Assert.True(config.Enabled);
    }

    [Fact]
    public async Task Create_SamlType_With_KnownFlavor_Succeeds()
    {
        // SAML support landed on feat/saml-federation. The flavor key must
        // match a registered ISamlFlavor (GenericSaml / EntraIdSaml / AdfsSaml).
        // A bare "AnyFlavor" string is rejected with UnknownFlavor, mirroring
        // the OIDC create gate.
        using var scope = Factory.Services.CreateScope();
        var bus = GetTenantedMessageBus(scope);

        var result = await bus.InvokeAsync<ErrorOr<LoginProvider>>(new CreateLoginProviderCommand(
            Flavor: LoginProviderFlavor.GenericSaml,
            DisplayName: $"Saml-Test-{Guid.NewGuid():N}"[..32],
            Slug: "saml-known-flavor",
            FlavorData: null,
            Type: LoginProviderType.Saml));

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        Assert.Equal(LoginProviderType.Saml, result.Value.Type);
        Assert.Equal(LoginProviderFlavor.GenericSaml, result.Value.Flavor);
        Assert.False(result.Value.Enabled); // SAML providers start disabled.
    }

    [Fact]
    public async Task Create_SamlType_With_UnknownFlavor_Rejected()
    {
        using var scope = Factory.Services.CreateScope();
        var bus = GetTenantedMessageBus(scope);

        var result = await bus.InvokeAsync<ErrorOr<LoginProvider>>(new CreateLoginProviderCommand(
            Flavor: "NotARealSamlFlavor",
            DisplayName: $"Saml-Bad-{Guid.NewGuid():N}"[..32],
            Slug: "saml-bad-flavor",
            FlavorData: null,
            Type: LoginProviderType.Saml));

        Assert.True(result.IsError);
        Assert.Equal("LoginProvider.UnknownFlavor", result.FirstError.Code);
    }

    [Fact]
    public async Task Create_FullForm_AppliesAllOptionalFields()
    {
        // Single-modal Add flow: admin submits everything in one Create call
        // instead of Create-then-Update. All optional fields land on the
        // resulting aggregate; nothing falls back to flavor defaults except
        // what the admin actually omitted.
        using var scope = Factory.Services.CreateScope();
        var bus = GetTenantedMessageBus(scope);

        var flavorData = JsonDocument.Parse("""{"MetadataUri": "https://idp.test/.well-known/openid-configuration"}""");
        var result = await bus.InvokeAsync<ErrorOr<LoginProvider>>(new CreateLoginProviderCommand(
            Flavor: LoginProviderFlavor.GenericOidc,
            DisplayName: $"Full-{Guid.NewGuid():N}"[..16],
            Slug: "full-form",
            FlavorData: flavorData,
            Type: LoginProviderType.Oidc,
            Description: "single-modal full submit",
            // Enabled stays at its default (false). The dedicated test below
            // covers atomic Enabled + InitialClientSecret creation.
            ClientId: "client-xyz",
            Scopes: ["openid", "profile", "email", "groups"],
            UserUpdateScript: "return { firstname: claims.given_name };",
            StoreRawClaims: true,
            RawClaimsRetentionDays: 30,
            AutoCreateUsers: true,
            AllowLinking: false,
            TrustForEmailLink: true,
            AllowedEmailDomains: ["acme.com"],
            IconName: "lucide-key",
            ButtonColorHex: "#0078D4"));

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : "");
        var c = result.Value;
        Assert.False(c.Enabled, "OIDC Create should land disabled — secret must be rotated first");
        Assert.Equal("client-xyz", c.ClientId);
        Assert.Equal(["openid", "profile", "email", "groups"], c.Scopes);
        Assert.Equal("return { firstname: claims.given_name };", c.UserUpdateScript);
        Assert.True(c.StoreRawClaims);
        Assert.Equal(30, c.RawClaimsRetentionDays);
        Assert.True(c.AutoCreateUsers);
        Assert.False(c.AllowLinking);
        Assert.True(c.TrustForEmailLink);
        Assert.Equal(["acme.com"], c.AllowedEmailDomains);
        Assert.Equal("lucide-key", c.IconName);
        Assert.Equal("#0078D4", c.ButtonColorHex);
        Assert.Equal("single-modal full submit", c.Description);
    }

    [Fact]
    public async Task Create_FullForm_OverlongUserUpdateScript_Rejected()
    {
        // ScriptInputLimits guard applies at Create time too when the admin
        // submits a UserUpdateScript via the single-modal flow — same defense
        // as the Update path.
        using var scope = Factory.Services.CreateScope();
        var bus = GetTenantedMessageBus(scope);

        var oversized = new string('x', 20 * 1024); // ScriptInputLimits caps at 16 KiB
        var flavorData = JsonDocument.Parse("""{"MetadataUri": "https://idp.test/.well-known/openid-configuration"}""");
        var result = await bus.InvokeAsync<ErrorOr<LoginProvider>>(new CreateLoginProviderCommand(
            Flavor: LoginProviderFlavor.GenericOidc,
            DisplayName: $"Oversize-{Guid.NewGuid():N}"[..20],
            Slug: "oversize-script",
            FlavorData: flavorData,
            UserUpdateScript: oversized));

        Assert.True(result.IsError);
        Assert.StartsWith("LoginProvider.UserUpdateScript", result.FirstError.Code);
    }

    [Fact]
    public async Task Create_OidcEnabledTrue_WithoutInitialSecret_Rejected()
    {
        // Readiness-gate parity with EnableLoginProviderHandler: an enabled
        // OIDC provider must still have a ClientSecret. Atomic create supports
        // one, but omitting it must remain unsafe.
        using var scope = Factory.Services.CreateScope();
        var bus = GetTenantedMessageBus(scope);

        var flavorData = JsonDocument.Parse("""{"MetadataUri": "https://idp.test/.well-known/openid-configuration"}""");
        var result = await bus.InvokeAsync<ErrorOr<LoginProvider>>(new CreateLoginProviderCommand(
            Flavor: LoginProviderFlavor.GenericOidc,
            DisplayName: $"OidcEnabled-{Guid.NewGuid():N}"[..18],
            Slug: "oidc-enabled",
            FlavorData: flavorData,
            Enabled: true,
            ClientId: "client-xyz"));

        Assert.True(result.IsError);
        Assert.Equal("LoginProvider.SecretRequired", result.FirstError.Code);
    }

    [Fact]
    public async Task Create_OidcEnabledTrue_WithInitialSecret_SucceedsAtomically()
    {
        // The expert modal submits the complete provider in one request. The
        // plaintext initial secret is encrypted before it enters the event and
        // the readiness gate evaluates that encrypted value in the same
        // command, so no create-then-rotate round-trip is needed.
        using var scope = Factory.Services.CreateScope();
        var bus = GetTenantedMessageBus(scope);

        const string initialSecret = "integration-test-secret";
        var flavorData = JsonDocument.Parse("""{"MetadataUri": "https://idp.test/.well-known/openid-configuration"}""");
        var result = await bus.InvokeAsync<ErrorOr<LoginProvider>>(new CreateLoginProviderCommand(
            Flavor: LoginProviderFlavor.GenericOidc,
            DisplayName: $"OidcReady-{Guid.NewGuid():N}"[..18],
            Slug: "oidc-ready",
            FlavorData: flavorData,
            Enabled: true,
            ClientId: "client-xyz",
            InitialClientSecret: initialSecret));

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : "");
        Assert.True(result.Value.Enabled);
        Assert.NotNull(result.Value.ClientSecretEncrypted);
        Assert.NotEqual(
            initialSecret,
            System.Text.Encoding.UTF8.GetString(result.Value.ClientSecretEncrypted!));

        var secretStore = scope.ServiceProvider.GetRequiredService<LoginProviderSecretStore>();
        Assert.Equal(initialSecret, secretStore.Decrypt(result.Value.ClientSecretEncrypted!));
    }

    [Fact]
    public async Task Create_SamlEnabledTrue_WithoutMetadata_Rejected()
    {
        // SAML readiness gate parity: a SAML provider needs IdP metadata
        // before the scheme manager can build a Saml2Configuration. Creating
        // it as Enabled=true with no MetadataUrl/Xml would register a half-
        // broken scheme that users hit as /login?error=saml-no-metadata.
        using var scope = Factory.Services.CreateScope();
        var bus = GetTenantedMessageBus(scope);

        var result = await bus.InvokeAsync<ErrorOr<LoginProvider>>(new CreateLoginProviderCommand(
            Flavor: LoginProviderFlavor.GenericSaml,
            DisplayName: $"SamlEnabled-{Guid.NewGuid():N}"[..18],
            Slug: "saml-enabled-nometa",
            FlavorData: null,
            Type: LoginProviderType.Saml,
            Enabled: true));

        Assert.True(result.IsError);
        Assert.Equal("LoginProvider.SamlMetadataRequired", result.FirstError.Code);
    }

    [Fact]
    public async Task Create_SamlEnabledTrue_WithMetadataUrl_Succeeds()
    {
        // Counter-test: if metadata IS present in FlavorData, Enabled=true at
        // Create is allowed (admin opts in explicitly with a fully-configured
        // provider). The single-modal Add flow always sends Enabled=false so
        // this path is only reachable via direct API calls.
        using var scope = Factory.Services.CreateScope();
        var bus = GetTenantedMessageBus(scope);

        var flavorData = JsonDocument.Parse("""{"metadataUrl": "https://idp.test/metadata.xml"}""");
        var result = await bus.InvokeAsync<ErrorOr<LoginProvider>>(new CreateLoginProviderCommand(
            Flavor: LoginProviderFlavor.GenericSaml,
            DisplayName: $"SamlOk-{Guid.NewGuid():N}"[..18],
            Slug: "saml-ok-meta",
            FlavorData: flavorData,
            Type: LoginProviderType.Saml,
            Enabled: true));

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        Assert.True(result.Value.Enabled);
    }

    [Theory]
    [InlineData(LoginProviderType.Ldap)]
    [InlineData(LoginProviderType.Kerberos)]
    public async Task Create_OtherUnsupportedTypes_ReturnSameErrorCode(LoginProviderType type)
    {
        // LDAP and Kerberos share the centralized unsupported-type error.
        // SAML is a supported protocol with its own flavor registry.
        using var scope = Factory.Services.CreateScope();
        var bus = GetTenantedMessageBus(scope);

        var result = await bus.InvokeAsync<ErrorOr<LoginProvider>>(new CreateLoginProviderCommand(
            Flavor: "AnyFlavor",
            DisplayName: $"Attempt-{type}-{Guid.NewGuid():N}"[..32],
            Slug: "unsupported-attempt",
            FlavorData: null,
            Type: type));

        Assert.True(result.IsError);
        Assert.Equal("LoginProvider.TypeNotSupported", result.FirstError.Code);
    }

    [Fact]
    public async Task Create_SecondInternal_Conflicts()
    {
        // Phase 2: at most one Internal provider per realm. The seeder writes
        // it on realm creation; admin Create with Type=Internal must reject
        // when one already exists. We seed a built-in Internal manually here
        // (the integration-test reset clears every realm DB).
        using (var scope = Factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Events.StartStream<LoginProvider>(Guid.NewGuid(), new LoginProviderAddedEvent(
                Id: Guid.NewGuid(),
                Type: LoginProviderType.Internal,
                Flavor: LoginProviderFlavor.Internal,
                Slug: LoginProviderSlugRules.InternalSlug,
                DisplayName: "Internal Authentication",
                Description: null,
                IsBuiltIn: true,
                Enabled: true,
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
                CreatedAt: DateTimeOffset.UtcNow));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using var scope2 = Factory.Services.CreateScope();
        var bus = GetTenantedMessageBus(scope2);

        var result = await bus.InvokeAsync<ErrorOr<LoginProvider>>(new CreateLoginProviderCommand(
            Flavor: string.Empty,
            DisplayName: "Second Internal Attempt",
            // Distinct slug from the seeded Internal ("internal") so the
            // single-Internal rule — not slug-uniqueness — is what trips.
            Slug: "second-internal",
            FlavorData: null,
            Type: LoginProviderType.Internal));

        Assert.True(result.IsError);
        Assert.Equal("LoginProvider.InternalAlreadyExists", result.FirstError.Code);
    }

    [Fact]
    public async Task Create_InvalidSlug_ValidationError()
    {
        // Slug grammar is enforced at command time before any type-specific
        // handling. An uppercase / malformed slug is rejected with a stable code.
        using var scope = Factory.Services.CreateScope();
        var bus = GetTenantedMessageBus(scope);

        var flavorData = JsonDocument.Parse("""{"MetadataUri": "https://idp.test/.well-known/openid-configuration"}""");
        var result = await bus.InvokeAsync<ErrorOr<LoginProvider>>(new CreateLoginProviderCommand(
            Flavor: LoginProviderFlavor.GenericOidc,
            DisplayName: "Bad Slug Provider",
            Slug: "Not_A_Valid_Slug",
            FlavorData: flavorData));

        Assert.True(result.IsError);
        Assert.Equal("LoginProvider.SlugInvalid", result.FirstError.Code);
    }

    [Fact]
    public async Task Create_DuplicateSlug_Conflicts()
    {
        // Slug is unique per realm. Two providers with distinct display names
        // but the same slug: the second is rejected with SlugTaken.
        using var scope = Factory.Services.CreateScope();
        var bus = GetTenantedMessageBus(scope);

        var flavorData = JsonDocument.Parse("""{"MetadataUri": "https://idp.test/.well-known/openid-configuration"}""");
        var first = await bus.InvokeAsync<ErrorOr<LoginProvider>>(new CreateLoginProviderCommand(
            Flavor: LoginProviderFlavor.GenericOidc,
            DisplayName: "Slug Owner One",
            Slug: "shared-slug",
            FlavorData: flavorData));
        Assert.False(first.IsError, first.IsError ? first.FirstError.Description : "");

        var second = await bus.InvokeAsync<ErrorOr<LoginProvider>>(new CreateLoginProviderCommand(
            Flavor: LoginProviderFlavor.GenericOidc,
            DisplayName: "Slug Owner Two",
            Slug: "shared-slug",
            FlavorData: flavorData));

        Assert.True(second.IsError);
        Assert.Equal("LoginProvider.SlugTaken", second.FirstError.Code);
    }

    [Fact]
    public async Task Update_BuiltInInternal_IsRejected()
    {
        // Phase 2: IsBuiltIn entries are immutable from the admin surface.
        // Seed a built-in Internal stream and verify the update command bounces.
        var id = Guid.NewGuid();
        using (var scope = Factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Events.StartStream<LoginProvider>(id, new LoginProviderAddedEvent(
                Id: id,
                Type: LoginProviderType.Internal,
                Flavor: LoginProviderFlavor.Internal,
                Slug: LoginProviderSlugRules.InternalSlug,
                DisplayName: "Internal Authentication",
                Description: null,
                IsBuiltIn: true,
                Enabled: true,
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
                CreatedAt: DateTimeOffset.UtcNow));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using var scope2 = Factory.Services.CreateScope();
        var bus = GetTenantedMessageBus(scope2);

        var result = await bus.InvokeAsync<ErrorOr<LoginProvider>>(new UpdateLoginProviderCommand(
            Id: id,
            DisplayName: "Renamed",
            Description: null,
            ClientId: string.Empty,
            Scopes: new List<string>(),
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
            Enabled: default));

        Assert.True(result.IsError);
        Assert.Equal("LoginProvider.InternalNotEditable", result.FirstError.Code);
    }
}
