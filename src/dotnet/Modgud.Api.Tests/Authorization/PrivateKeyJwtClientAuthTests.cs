using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Application.DTOs.OAuth;
using Modgud.Application.Services;
using Modgud.Authentication.RateLimiting;
using Modgud.Domain.Common;
using Modgud.Domain.OAuth.Common;

namespace Modgud.Api.Tests.Authorization;

/// <summary>
/// <c>private_key_jwt</c> client authentication (RFC 7523 / OIDC Core §9): a confidential
/// client registers a public key set and authenticates with a signed assertion instead of
/// a shared secret — at the token endpoint and, for the trusted-forwarder capability, in
/// the auth caller context. Admin-managed (user-flow) clients; service-account credentials
/// keep their own secret lifecycle.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class PrivateKeyJwtClientAuthTests(SharedPostgresFixture fixture) : IntegrationTestBase(fixture)
{
    [Fact]
    public async Task A_client_with_a_key_set_and_no_secret_authenticates_with_a_signed_assertion()
    {
        var ct = TestContext.Current.CancellationToken;
        using var keys = new TestJwks("pkj-1");
        var (clientId, created) = await CreateClientAsync(keys.PublicJwks, secret: null);
        Assert.Null(created.ClientSecret);
        Assert.False(created.Client.HasClientSecret);
        Assert.NotNull(created.Client.JsonWebKeySet);

        using var doc = await TokenAsync(clientId, MintAssertion(clientId, keys.SigningCredentials, await IssuerAsync()));
        Assert.Equal("invalid_grant", doc.RootElement.GetProperty("error").GetString());

        // No secret exists: client_secret_post is refused.
        using var withSecret = await TokenAsync(clientId, assertion: null, clientSecret: "anything");
        Assert.Equal("invalid_client", withSecret.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task An_assertion_signed_with_an_unregistered_key_is_refused()
    {
        using var registered = new TestJwks("pkj-good");
        using var rogue = new TestJwks("pkj-good"); // same kid, different key
        var (clientId, _) = await CreateClientAsync(registered.PublicJwks, secret: null);

        using var doc = await TokenAsync(clientId, MintAssertion(clientId, rogue.SigningCredentials, await IssuerAsync()));
        Assert.Equal("invalid_client", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_secret_and_a_key_set_may_coexist_and_the_key_set_can_be_replaced_or_removed()
    {
        var ct = TestContext.Current.CancellationToken;
        using var first = new TestJwks("pkj-a");
        using var second = new TestJwks("pkj-b");
        var (clientId, created) = await CreateClientAsync(first.PublicJwks, secret: "pkj-shared-secret-123456");
        Assert.True(created.Client.HasClientSecret);
        var issuer = await IssuerAsync();

        using (var doc = await TokenAsync(clientId, MintAssertion(clientId, first.SigningCredentials, issuer)))
            Assert.Equal("invalid_grant", doc.RootElement.GetProperty("error").GetString());

        // Rotate to a new key: the old one stops working, the new one works.
        using var scope = Factory.Services.CreateScope();
        var admin = scope.ServiceProvider.GetRequiredService<OAuthAdminService>();
        var rotated = await admin.UpdateClientAsync(created.Client.Id, new UpdateOAuthClientDto { JsonWebKeySet = new Optional<string?>(second.PublicJwks) }, ct);
        Assert.False(rotated.IsError, rotated.IsError ? rotated.FirstError.Description : "");
        using (var doc = await TokenAsync(clientId, MintAssertion(clientId, first.SigningCredentials, issuer)))
            Assert.Equal("invalid_client", doc.RootElement.GetProperty("error").GetString());
        using (var doc = await TokenAsync(clientId, MintAssertion(clientId, second.SigningCredentials, issuer)))
            Assert.Equal("invalid_grant", doc.RootElement.GetProperty("error").GetString());

        // Remove the key set (a secret remains, so the client keeps a credential).
        var removed = await admin.UpdateClientAsync(created.Client.Id, new UpdateOAuthClientDto { JsonWebKeySet = new Optional<string?>(null) }, ct);
        Assert.False(removed.IsError, removed.IsError ? removed.FirstError.Description : "");
        Assert.Null(removed.Value.JsonWebKeySet);
        using (var doc = await TokenAsync(clientId, MintAssertion(clientId, second.SigningCredentials, issuer)))
            Assert.Equal("invalid_client", doc.RootElement.GetProperty("error").GetString());
        using (var doc = await TokenAsync(clientId, assertion: null, clientSecret: "pkj-shared-secret-123456"))
            Assert.Equal("invalid_grant", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Removing_the_only_credential_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        using var keys = new TestJwks("pkj-only");
        var (_, created) = await CreateClientAsync(keys.PublicJwks, secret: null);
        using var scope = Factory.Services.CreateScope();
        var admin = scope.ServiceProvider.GetRequiredService<OAuthAdminService>();
        var result = await admin.UpdateClientAsync(created.Client.Id, new UpdateOAuthClientDto { JsonWebKeySet = new Optional<string?>(null) }, ct);
        Assert.True(result.IsError);
        Assert.Equal("OAuth.InvalidJsonWebKeySet", result.FirstError.Code);
    }

    [Theory]
    [InlineData("{}", "keys")]
    [InlineData("{\"keys\":[]}", "empty")]
    [InlineData("{\"keys\":[{\"kty\":\"oct\",\"kid\":\"a\",\"k\":\"x\"}]}", "RSA and EC")]
    [InlineData("not json", "JSON")]
    public async Task Malformed_key_sets_are_rejected(string jwks, string expectedReason)
    {
        var ct = TestContext.Current.CancellationToken;
        using var scope = Factory.Services.CreateScope();
        var admin = scope.ServiceProvider.GetRequiredService<OAuthAdminService>();
        var result = await admin.CreateClientAsync(BuildCreateDto($"pkj-bad-{Guid.NewGuid():N}", jwks, null), ct);
        Assert.True(result.IsError);
        Assert.Equal("OAuth.InvalidJsonWebKeySet", result.FirstError.Code);
        Assert.Contains(expectedReason, result.FirstError.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_private_key_or_a_key_without_kid_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        using var keys = new TestJwks("pkj-priv");
        using var scope = Factory.Services.CreateScope();
        var admin = scope.ServiceProvider.GetRequiredService<OAuthAdminService>();

        var withPrivate = await admin.CreateClientAsync(BuildCreateDto($"pkj-priv-{Guid.NewGuid():N}", $"{{\"keys\":[{keys.PrivateJwk}]}}", null), ct);
        Assert.True(withPrivate.IsError);
        Assert.Contains("private member", withPrivate.FirstError.Description);

        var noKid = keys.PublicJwks.Replace("\"kid\":\"pkj-priv\",", "");
        var withoutKid = await admin.CreateClientAsync(BuildCreateDto($"pkj-nokid-{Guid.NewGuid():N}", noKid, null), ct);
        Assert.True(withoutKid.IsError);
        Assert.Contains("kid", withoutKid.FirstError.Description);
    }

    [Fact]
    public async Task A_trusted_forwarder_may_authenticate_its_caller_context_with_an_assertion()
    {
        var ct = TestContext.Current.CancellationToken;
        using var keys = new TestJwks("pkj-fwd");
        var (clientId, _) = await CreateClientAsync(keys.PublicJwks, secret: null, capabilities: ["cap:trusted-forwarder"]);
        var issuer = await IssuerAsync();

        using var scope = Factory.Services.CreateScope();
        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        http.Items["TenantId"] = "system";
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = http;
        http.Request.Method = "POST";
        http.Request.Scheme = "http";
        http.Request.Host = new HostString("localhost");
        http.Request.ContentType = "application/x-www-form-urlencoded";
        http.Request.Form = new FormCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
        {
            ["client_assertion_type"] = ClientAssertionValidator.JwtBearerAssertionType,
            ["client_assertion"] = MintAssertion(clientId, keys.SigningCredentials, issuer),
        });
        http.Request.Headers["Modgud-Forwarded-For"] = "203.0.113.9";
        http.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.5");

        var factory = scope.ServiceProvider.GetRequiredService<IAuthCallerContextFactory>();
        var result = await factory.BuildAsync(http, ct);
        Assert.True(result.Context is not null, result.ErrorCode + " " + result.ErrorDetail);
        Assert.Equal(clientId, result.Context!.ClientId);
        Assert.Equal("203.0.113.9", result.Context.EffectiveAddress?.ToString());

        // A forged assertion (unregistered key) is not a trusted forwarder: the header is refused.
        using var rogue = new TestJwks("pkj-fwd");
        http.Request.Form = new FormCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
        {
            ["client_assertion_type"] = ClientAssertionValidator.JwtBearerAssertionType,
            ["client_assertion"] = MintAssertion(clientId, rogue.SigningCredentials, issuer),
        });
        var forged = await factory.BuildAsync(http, ct);
        Assert.Equal(AuthCallerContextFactory.ErrorForwarderNotTrusted, forged.ErrorCode);
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static CreateOAuthClientDto BuildCreateDto(string clientId, string? jwks, string? secret, List<string>? capabilities = null) => new()
    {
        ClientId = clientId,
        ClientSecret = secret,
        JsonWebKeySet = jwks,
        ClientType = OAuthClientTypes.Confidential,
        ConsentType = OAuthConsentTypes.Implicit,
        DisplayName = clientId,
        RedirectUris = ["https://pkj.example/cb"],
        Scopes = ["openid"],
        AllowedGrantTypes = ["authorization_code", "refresh_token"],
        Capabilities = capabilities ?? [],
        AccessTokenType = AccessTokenType.Jwt,
    };

    private async Task<(string ClientId, OAuthClientCreatedDto Created)> CreateClientAsync(string jwks, string? secret, List<string>? capabilities = null)
    {
        var clientId = $"pkj-{Guid.NewGuid():N}"[..24];
        using var scope = Factory.Services.CreateScope();
        var admin = scope.ServiceProvider.GetRequiredService<OAuthAdminService>();
        var result = await admin.CreateClientAsync(BuildCreateDto(clientId, jwks, secret, capabilities), TestContext.Current.CancellationToken);
        if (result.IsError) throw new InvalidOperationException(string.Join(", ", result.Errors.Select(e => $"{e.Code}: {e.Description}")));
        return (clientId, result.Value);
    }

    private async Task<string> IssuerAsync()
    {
        using var doc = JsonDocument.Parse(await Factory.CreateClient().GetStringAsync("/.well-known/openid-configuration", TestContext.Current.CancellationToken));
        return doc.RootElement.GetProperty("token_endpoint").GetString()!;
    }

    private static string MintAssertion(string clientId, SigningCredentials credentials, string audience)
    {
        var now = DateTime.UtcNow;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = clientId,
            Audience = audience,
            Subject = new ClaimsIdentity([new Claim("sub", clientId)]),
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddMinutes(2),
            SigningCredentials = credentials,
            // OpenIddict (and RFC 7523bis) expect the explicit JWT type for client assertions.
            TokenType = "client-authentication+jwt",
            Claims = new Dictionary<string, object> { ["jti"] = Guid.NewGuid().ToString("N") },
        });
    }

    private async Task<JsonDocument> TokenAsync(string clientId, string? assertion, string? clientSecret = null)
    {
        // A bogus refresh token: an authenticated client gets invalid_grant, an
        // unauthenticated one invalid_client — which is what these tests decide.
        var form = new List<KeyValuePair<string, string>>
        {
            new("grant_type", "refresh_token"),
            new("refresh_token", "not-a-real-refresh-token"),
            new("client_id", clientId),
        };
        if (assertion is not null)
        {
            form.Add(new("client_assertion_type", ClientAssertionValidator.JwtBearerAssertionType));
            form.Add(new("client_assertion", assertion));
        }
        if (clientSecret is not null) form.Add(new("client_secret", clientSecret));
        var response = await Factory.CreateClient().PostAsync("/connect/token", new FormUrlEncodedContent(form), TestContext.Current.CancellationToken);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }
}
