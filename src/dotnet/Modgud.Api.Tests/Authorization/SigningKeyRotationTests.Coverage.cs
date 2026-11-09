using System.Reflection;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Modgud.Api.Features.Admin.Jobs;
using Modgud.Application.DTOs.Realms;
using Modgud.Domain.Realms;
using Modgud.Infrastructure.Audit;
using Modgud.Infrastructure.Persistence.Tenancy;
using Modgud.Infrastructure.Realms;
using OpenIddict.Server;
using Quartz;

namespace Modgud.Api.Tests.Authorization;

/// <summary>
/// FK-11 — the parts of the key lifecycle the store-level tests did not reach: what the
/// JWKS endpoint actually publishes (only the realm's keys, never the global
/// certificate), that two realms publish disjoint key sets, that the janitor job (not
/// only the store method) purges, and what a concurrent first-key bootstrap leaves.
/// </summary>
public partial class SigningKeyRotationTests
{
    [Fact]
    public async Task Jwks_publishes_exactly_the_realm_verification_keys_and_never_the_global_certificate()
    {
        var ct = TestContext.Current.CancellationToken;
        var keyStore = Factory.Services.GetRequiredService<IRealmKeyStore>();
        // On the reset tenant the first rotation creates the first key; the second retires
        // it: one active plus one retired key inside the overlap.
        await keyStore.RotateAsync(Realm, ct);
        await keyStore.RotateAsync(Realm, ct);
        var expected = (await keyStore.GetVerificationKeysAsync(Realm, ct)).OfType<RsaSecurityKey>().ToList();
        Assert.Equal(2, expected.Count);

        var jwks = new JsonWebKeySet(await Factory.CreateClient().GetStringAsync("/.well-known/jwks", ct));

        Assert.Equal(expected.Select(k => k.KeyId).ToHashSet(), jwks.Keys.Select(k => k.Kid).ToHashSet());
        foreach (var jwk in jwks.Keys)
        {
            Assert.Equal(("RSA", "sig", SecurityAlgorithms.RsaSha256), (jwk.Kty, jwk.Use, jwk.Alg));
            var key = expected.Single(k => k.KeyId == jwk.Kid);
            var parameters = key.Rsa?.ExportParameters(false) ?? key.Parameters;
            Assert.Equal(Base64UrlEncoder.Encode(parameters.Modulus!.SkipWhile(b => b == 0).ToArray()), jwk.N);
            Assert.Null(jwk.D); // public material only
        }

        // The server's global pool (dev certificate here) signs IdP-internal tokens only.
        var global = Factory.Services.GetRequiredService<IOptionsMonitor<OpenIddictServerOptions>>()
            .CurrentValue.SigningCredentials.Select(c => c.Key).ToList();
        Assert.NotEmpty(global);
        foreach (var key in global)
        {
            Assert.DoesNotContain(jwks.Keys, j => j.Kid == key.KeyId);
            if (key is X509SecurityKey x509)
                Assert.DoesNotContain(jwks.Keys, j => j.X5t == Base64UrlEncoder.Encode(x509.Certificate.GetCertHash()));
        }
    }

    [Fact]
    public async Task Jwks_of_two_realms_share_no_key()
    {
        var ct = TestContext.Current.CancellationToken;
        var slug = $"jwks-{Guid.NewGuid():N}"[..14];
        var created = await Factory.Services.GetRequiredService<IRealmProvisioningService>()
            .CreateRealmAsync(new CreateRealmDto
            {
                Slug = slug, DisplayName = "JWKS separation", Domains = [$"{slug}.localhost"], IsActive = true,
            }, ct);
        Assert.False(created.IsError, created.IsError ? created.FirstError.Description : "");

        var system = await JwksAsync(host: null);
        var other = await JwksAsync(host: $"{slug}.localhost");

        Assert.NotEmpty(system.Keys);
        Assert.NotEmpty(other.Keys);
        Assert.Empty(system.Keys.Select(k => k.Kid).Intersect(other.Keys.Select(k => k.Kid)));
        Assert.Empty(system.Keys.Select(k => k.N).Intersect(other.Keys.Select(k => k.N)));

        async Task<JsonWebKeySet> JwksAsync(string? host)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/.well-known/jwks");
            if (host is not null) request.Headers.Host = host;
            var response = await Factory.CreateClient().SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            Assert.True(response.IsSuccessStatusCode, body);
            return new JsonWebKeySet(body);
        }
    }

    [Fact]
    public async Task Janitor_job_purges_the_expired_retired_keys_of_its_realm()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var sut = NewStore(clock);
        var oldKid = (await sut.GetActiveSigningCredentialsAsync(Realm, ct)).Key.KeyId;
        var newKid = (await sut.RotateAsync(Realm, ct)).Key.KeyId;
        clock.Advance(RealmKeyStore.RotationOverlap + TimeSpan.FromDays(1));

        var context = DispatchProxy.Create<IJobExecutionContext, JobContextProxy>();
        using (TenantContext.Enter(Realm))
            await new SigningKeyJanitorJob(sut, Factory.Services.GetRequiredService<ISecurityAuditLog>()).Execute(context);

        Assert.Equal("Purged 1 expired signing key(s)", context.Result);
        await using var session = GetTenantedSession(Realm);
        var remaining = await session.Query<RealmSigningKey>().ToListAsync(ct);
        Assert.DoesNotContain(remaining, k => k.KeyId == oldKid);
        Assert.Contains(remaining, k => k.KeyId == newKid && k.IsActive);
    }

    [Fact]
    public async Task A_concurrent_first_key_bootstrap_leaves_every_active_key_verifiable()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new TestClock(DateTimeOffset.UtcNow);
        // Two instances, cold: both may find no active key and create one (there is no
        // database constraint on "one active key"; the next rotation retires all). What
        // must hold meanwhile is that whichever key an instance signs with verifies.
        var a = NewStore(clock);
        var b = NewStore(clock);
        var kids = await Task.WhenAll(
            Task.Run(async () => (await a.GetActiveSigningCredentialsAsync(Realm, ct)).Key.KeyId, ct),
            Task.Run(async () => (await b.GetActiveSigningCredentialsAsync(Realm, ct)).Key.KeyId, ct));

        var verifying = Kids(await NewStore(clock).GetVerificationKeysAsync(Realm, ct));
        Assert.Contains(kids[0], verifying);
        Assert.Contains(kids[1], verifying);

        await using var session = GetTenantedSession(Realm);
        var actives = await session.Query<RealmSigningKey>().Where(k => k.IsActive).ToListAsync(ct);
        Assert.InRange(actives.Count, 1, 2);
    }

    /// <summary>The two members the janitor touches; anything else is a test error.</summary>
    public class JobContextProxy : DispatchProxy
    {
        private object? _result;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
        {
            "get_CancellationToken" => TestContext.Current.CancellationToken,
            "get_Result" => _result,
            "set_Result" => _result = args![0],
            _ => throw new NotSupportedException(targetMethod?.Name),
        };
    }
}
