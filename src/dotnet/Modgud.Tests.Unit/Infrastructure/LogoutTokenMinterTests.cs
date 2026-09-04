using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Modgud.Authentication.BackChannelLogout;
using Modgud.Infrastructure.Realms;

namespace Modgud.Tests.Unit.Infrastructure;

/// <summary>ADR 0009 — the logout token follows OpenID Connect Back-Channel Logout 1.0 §2.4.</summary>
public class LogoutTokenMinterTests
{
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class SingleKeyStore : IRealmKeyStore
    {
        public RsaSecurityKey Key { get; } = new(RSA.Create(2048)) { KeyId = "kid-test" };

        public Task<SigningCredentials> GetActiveSigningCredentialsAsync(string realmSlug, CancellationToken ct = default) =>
            Task.FromResult(new SigningCredentials(Key, SecurityAlgorithms.RsaSha256));

        public Task<IReadOnlyList<SecurityKey>> GetVerificationKeysAsync(string realmSlug, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SecurityKey>>([Key]);

        public Task<SigningCredentials> RotateAsync(string realmSlug, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<int> PurgeExpiredRetiredKeysAsync(string realmSlug, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private static readonly DateTimeOffset T0 = new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Mints_a_spec_shaped_token_signed_with_the_realm_key()
    {
        var keys = new SingleKeyStore();
        var minter = new LogoutTokenMinter(keys, new FixedClock(T0));

        var token = await minter.MintAsync("acme", "https://auth.acme.test/", "rp-one", "user-1", "sid-1");

        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = "https://auth.acme.test/",
            ValidAudience = "rp-one",
            IssuerSigningKey = keys.Key,
            ValidTypes = [LogoutTokenMinter.TokenType],
            ValidateLifetime = false,
        });
        Assert.True(result.IsValid, result.Exception?.ToString());

        var jwt = (JsonWebToken)result.SecurityToken;
        Assert.Equal("kid-test", jwt.Kid);
        Assert.Equal("RS256", jwt.Alg);
        Assert.Equal("user-1", jwt.Subject);
        Assert.Equal("sid-1", jwt.GetClaim("sid").Value);
        Assert.Equal(T0.UtcDateTime, jwt.IssuedAt);
        Assert.Equal(T0.Add(BackChannelLogoutConstants.TokenLifetime).UtcDateTime, jwt.ValidTo);
        Assert.False(string.IsNullOrEmpty(jwt.Id));
        Assert.False(jwt.TryGetClaim("nonce", out _));

        using var events = JsonDocument.Parse(jwt.GetClaim("events").Value);
        Assert.Equal(JsonValueKind.Object, events.RootElement.GetProperty(BackChannelLogoutConstants.EventUri).ValueKind);
    }

    [Fact]
    public async Task A_user_level_token_carries_no_sid_and_every_mint_is_fresh()
    {
        var minter = new LogoutTokenMinter(new SingleKeyStore(), new FixedClock(T0));

        var first = new JsonWebToken(await minter.MintAsync("acme", "https://auth.acme.test/", "rp", "user-1", null));
        var second = new JsonWebToken(await minter.MintAsync("acme", "https://auth.acme.test/", "rp", "user-1", null));

        Assert.False(first.TryGetClaim("sid", out _));
        Assert.NotEqual(first.Id, second.Id);
    }
}
