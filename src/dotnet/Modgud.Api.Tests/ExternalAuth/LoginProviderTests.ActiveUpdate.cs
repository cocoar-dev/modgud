using System.Text.Json;
using ErrorOr;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Authentication.Api.Admin.LoginProviders.Commands;
using Modgud.Authentication.Domain.LoginProviders;
using Modgud.Authentication.Identity.LoginProviders;
using Wolverine;

namespace Modgud.Api.Tests.ExternalAuth;

/// <summary>
/// EP-05 — the readiness gate ran only on the disabled → enabled transition, so an update
/// of an already active OIDC provider could persist an unusable configuration (an empty
/// ClientId) while staying enabled; the old registration then kept answering logins.
/// An update that would leave an enabled provider unusable is now refused.
/// </summary>
public partial class LoginProviderTests
{
    [Fact]
    public async Task Update_of_an_active_provider_to_an_unusable_configuration_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        using var scope = Factory.Services.CreateScope();
        var bus = GetTenantedMessageBus(scope);
        var created = await bus.InvokeAsync<ErrorOr<LoginProvider>>(new CreateLoginProviderCommand(
            Flavor: LoginProviderFlavor.GenericOidc,
            DisplayName: $"Active-{Guid.NewGuid():N}"[..18],
            Slug: $"a{Guid.NewGuid():N}"[..12],
            FlavorData: JsonDocument.Parse("""{"MetadataUri": "https://idp.test/.well-known/openid-configuration"}"""),
            Enabled: true,
            ClientId: "client-active",
            InitialClientSecret: "integration-test-secret"));
        Assert.False(created.IsError, created.IsError ? created.FirstError.Description : "");

        var update = await bus.InvokeAsync<ErrorOr<LoginProvider>>(new UpdateLoginProviderCommand(
            Id: created.Value.Id,
            DisplayName: default, Description: default,
            ClientId: string.Empty,
            Scopes: default, UserUpdateScript: default, StoreRawClaims: default, RawClaimsRetentionDays: default,
            AutoCreateUsers: default, AllowLinking: default, TrustForEmailLink: default, AllowedEmailDomains: default,
            IconName: default, ButtonColorHex: default, FlavorData: default, Enabled: default));

        Assert.True(update.IsError, "an active provider must not be updated into an unusable configuration");
        await using var query = GetTenantedSession();
        var stored = await query.LoadAsync<LoginProvider>(created.Value.Id, ct);
        Assert.Equal("client-active", stored!.ClientId);
        Assert.True(stored.Enabled);
    }
}
