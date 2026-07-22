using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Application.DTOs.OAuth;
using Modgud.Application.Services;
using Modgud.Domain.OAuth.Common;

namespace Modgud.Api.Tests.Security;

/// <summary>
/// Pins that the OAuth-2.1-removed <c>implicit</c> and <c>password</c> grants —
/// and any other unsupported grant — are rejected at client configuration time,
/// not merely inert at the token endpoint.
///
/// <para>
/// They used to be selectable in the admin UI and were silently persisted as
/// <c>gt:implicit</c> / <c>gt:password</c> permissions (they never functioned,
/// because the server enables neither flow). They are now gone from the UI and
/// refused by <see cref="OAuthAdminService"/> on both create and update, so a
/// client can never carry them — the "deliberately rejected" claim in the
/// security-model doc is now true at the config layer, not just by omission.
/// </para>
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class OAuthGrantTypeAllowlistTests : IntegrationTestBase
{
    public OAuthGrantTypeAllowlistTests(SharedPostgresFixture fixture) : base(fixture) { }

    private OAuthAdminService Admin(IServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<OAuthAdminService>();

    private static CreateOAuthClientDto NewClient(string clientId, List<string> grants) => new()
    {
        ClientId = clientId,
        ClientType = OAuthClientTypes.Public,
        ConsentType = OAuthConsentTypes.Explicit,
        DisplayName = clientId,
        RedirectUris = [],
        PostLogoutRedirectUris = [],
        Scopes = ["openid"],
        AllowedGrantTypes = grants,
        RequireConsent = false,
    };

    [Theory]
    [InlineData("implicit")]
    [InlineData("password")]
    public async Task Create_with_a_removed_grant_is_rejected(string removedGrant)
    {
        var ct = TestContext.Current.CancellationToken;
        using var scope = Factory.Services.CreateScope();

        var result = await Admin(scope).CreateClientAsync(
            NewClient($"reject-{removedGrant}-{Guid.NewGuid():N}",
                ["authorization_code", removedGrant]), ct);

        Assert.True(result.IsError);
        Assert.Equal("OAuth.UnsupportedGrantType", result.FirstError.Code);
        Assert.Contains(removedGrant, result.FirstError.Description);
    }

    [Fact]
    public async Task Update_to_add_a_removed_grant_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        using var scope = Factory.Services.CreateScope();
        var admin = Admin(scope);

        // A valid client first.
        var created = await admin.CreateClientAsync(
            NewClient($"upd-{Guid.NewGuid():N}", ["authorization_code", "refresh_token"]), ct);
        Assert.False(created.IsError,
            created.IsError ? created.FirstError.Description : "");

        // Now try to sneak the removed grant in via update.
        var updated = await admin.UpdateClientAsync(created.Value.Client.Id, new UpdateOAuthClientDto
        {
            AllowedGrantTypes = ["authorization_code", "password"],
        }, ct);

        Assert.True(updated.IsError);
        Assert.Equal("OAuth.UnsupportedGrantType", updated.FirstError.Code);
    }

    [Fact]
    public async Task Create_with_only_supported_grants_still_succeeds()
    {
        var ct = TestContext.Current.CancellationToken;
        using var scope = Factory.Services.CreateScope();

        // Positive control: the guard must not break the supported set.
        var result = await Admin(scope).CreateClientAsync(
            NewClient($"ok-{Guid.NewGuid():N}", ["authorization_code", "refresh_token"]), ct);

        Assert.False(result.IsError,
            result.IsError ? result.FirstError.Description : "");
    }
}
