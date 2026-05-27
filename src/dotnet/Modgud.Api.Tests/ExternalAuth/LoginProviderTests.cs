using System.Text.Json;
using ErrorOr;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Authentication.Api.Admin.LoginProviders.Commands;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Authentication.Domain.LoginProviders;
using Modgud.Authentication.Domain.LoginProviders.Events;
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
        await bus.InvokeAsync<ErrorOr<LoginProvider>>(new CreateLoginProviderCommand(LoginProviderFlavor.GenericOidc, "Duplicate", flavorData));

        var second = await bus.InvokeAsync<ErrorOr<LoginProvider>>(new CreateLoginProviderCommand(LoginProviderFlavor.GenericOidc, "Duplicate", flavorData));
        Assert.True(second.IsError);
        Assert.Equal("LoginProvider.DisplayNameTaken", second.FirstError.Code);
    }

    [Fact]
    public async Task Create_UnknownFlavor_ValidationError()
    {
        using var scope = Factory.Services.CreateScope();
        var bus = GetTenantedMessageBus(scope);

        var result = await bus.InvokeAsync<ErrorOr<LoginProvider>>(
            new CreateLoginProviderCommand("NopeFlavor", "X", null));

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
            new CreateLoginProviderCommand(LoginProviderFlavor.EntraId, "NoTenant", null));

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
            FlavorData: flavorData,
            Type: LoginProviderType.Oidc,
            Description: "single-modal full submit",
            Enabled: true,
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
        Assert.True(c.Enabled);
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
            FlavorData: flavorData,
            UserUpdateScript: oversized));

        Assert.True(result.IsError);
        Assert.StartsWith("LoginProvider.UserUpdateScript", result.FirstError.Code);
    }

    [Theory]
    [InlineData(LoginProviderType.Ldap)]
    [InlineData(LoginProviderType.Kerberos)]
    public async Task Create_OtherUnsupportedTypes_ReturnSameErrorCode(LoginProviderType type)
    {
        // Phase 2: TypeNotSupported is a single centralized error — Saml/Ldap/
        // Kerberos all share the same code so the frontend can render one message.
        using var scope = Factory.Services.CreateScope();
        var bus = GetTenantedMessageBus(scope);

        var result = await bus.InvokeAsync<ErrorOr<LoginProvider>>(new CreateLoginProviderCommand(
            Flavor: "AnyFlavor",
            DisplayName: $"Attempt-{type}-{Guid.NewGuid():N}"[..32],
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
            FlavorData: null,
            Type: LoginProviderType.Internal));

        Assert.True(result.IsError);
        Assert.Equal("LoginProvider.InternalAlreadyExists", result.FirstError.Code);
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
            FlavorData: null));

        Assert.True(result.IsError);
        Assert.Equal("LoginProvider.InternalNotEditable", result.FirstError.Code);
    }
}
