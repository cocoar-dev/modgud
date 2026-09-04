using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BuildingBlocks.Helper;
using Marten;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Application.DTOs.OAuth;
using Modgud.Application.DTOs.RealmSettings;
using Modgud.Application.Services;
using Modgud.Authentication.RealmSettings;
using Modgud.Authentication.Registration;
using Modgud.Authorization.Apps;
using Modgud.Authorization.Events;
using Modgud.Domain.Common;
using Modgud.Domain.OAuth.Common;
using Modgud.Domain.Realms;
using Modgud.Infrastructure.Email;
using Modgud.Infrastructure.Persistence.Tenancy;
using Modgud.Infrastructure.RateLimiting;
using Modgud.Infrastructure.Realms;

namespace Modgud.Api.Tests.Authorization;

/// <summary>
/// ADR 0007 — caller context and multi-dimensional rate limiting end-to-end: the 429
/// contract, target vs source roles, the capability-gated forwarder header, the
/// source allowlist, log-only mode, the silent registration ceiling, multi-instance
/// correctness of the Postgres counters, and the client-capability admin path.
///
/// <para>Testing partition hack (kept from the previous limiter): the test host has no
/// remote address, so each request is its own source unless it opts into a shared
/// one via <c>X-Test-RateLimit</c>. A forwarded address always yields a real key.</para>
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class AuthRateLimitTests : IntegrationTestBase
{
    public AuthRateLimitTests(SharedPostgresFixture fixture) : base(fixture) { }

    private const string Endpoint = "/api/account/native/otp/request";

    private sealed record Limited(string Error, string Policy, string Dimension, int RetryAfterSeconds);

    [Fact]
    public async Task Lowered_source_ceiling_throttles_and_the_429_carries_the_contract()
    {
        var ct = TestContext.Current.CancellationToken;
        await PatchAsync(Policy("native-otp", source: Rule(2, 60)), mode: RateLimitEnforcementMode.Enforce);
        var anon = Factory.CreateClient();

        Assert.NotEqual(HttpStatusCode.TooManyRequests, (await SendAsync(anon, "src-lowered", "p1@nowhere.example")).StatusCode);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, (await SendAsync(anon, "src-lowered", "p2@nowhere.example")).StatusCode);

        var rejected = await SendAsync(anon, "src-lowered", "p3@nowhere.example");
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.True(rejected.Headers.TryGetValues("Retry-After", out var retry) && int.Parse(retry.First()) > 0);
        var body = await rejected.Content.ReadFromJsonAsync<Limited>(JsonOptions, ct);
        Assert.NotNull(body);
        Assert.Equal("rate_limited", body!.Error);
        Assert.Equal("native-otp", body.Policy);
        Assert.Equal("source", body.Dimension);
        Assert.True(body.RetryAfterSeconds > 0);
    }

    [Fact]
    public async Task The_same_target_is_limited_across_rotating_sources()
    {
        var ct = TestContext.Current.CancellationToken;
        await PatchAsync(Policy("native-otp", source: null, target: Rule(5, 60)), mode: RateLimitEnforcementMode.Enforce);
        var anon = Factory.CreateClient();
        const string victim = "victim-target@nowhere.example";

        // No shared budget header → every request is its own source. Only the target
        // dimension can trip.
        for (var i = 0; i < 5; i++)
            Assert.NotEqual(HttpStatusCode.TooManyRequests, (await SendAsync(anon, budget: null, victim)).StatusCode);

        var rejected = await SendAsync(anon, budget: null, victim);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        var body = await rejected.Content.ReadFromJsonAsync<Limited>(JsonOptions, ct);
        Assert.Equal("target", body!.Dimension);
    }

    [Fact]
    public async Task Log_only_mode_never_rejects()
    {
        await PatchAsync(Policy("native-otp", source: Rule(1, 60)), mode: RateLimitEnforcementMode.LogOnly);
        var anon = Factory.CreateClient();
        for (var i = 0; i < 4; i++)
            Assert.NotEqual(HttpStatusCode.TooManyRequests, (await SendAsync(anon, "log-only", $"lo{i}@nowhere.example")).StatusCode);
    }

    [Fact]
    public async Task Forwarded_address_is_refused_without_the_capability_and_separates_users_with_it()
    {
        var ct = TestContext.Current.CancellationToken;
        await CreateConfidentialClientAsync("rl-fwd-plain", capabilities: []);
        await CreateConfidentialClientAsync("rl-fwd", capabilities: [OAuthPermissions.Capabilities.TrustedForwarder]);
        await PatchAsync(Policy("native-otp", source: Rule(1, 60)), mode: RateLimitEnforcementMode.Enforce);
        var anon = Factory.CreateClient();

        // Anonymous with the header → 400, never trusted.
        var forged = await SendAsync(anon, null, "forged@nowhere.example", forwardedFor: "10.1.1.1");
        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        Assert.Contains("Auth.ForwarderNotTrusted", await forged.Content.ReadAsStringAsync(ct));

        // Confidential but without the capability → 400 as well.
        var plain = await SendAsync(anon, null, "plain@nowhere.example", forwardedFor: "10.1.1.1", basic: ("rl-fwd-plain", "rl-fwd-plain-secret"));
        Assert.Equal(HttpStatusCode.BadRequest, plain.StatusCode);
        Assert.Contains("Auth.ForwarderNotTrusted", await plain.Content.ReadAsStringAsync(ct));

        // Entitled but silent about the user → 400.
        var missing = await SendAsync(anon, null, "missing@nowhere.example", basic: ("rl-fwd", "rl-fwd-secret"));
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Contains("Auth.ForwardedAddressRequired", await missing.Content.ReadAsStringAsync(ct));

        // Entitled: two browsers behind one BFF get their own source buckets.
        var a1 = await SendAsync(anon, null, "a1@nowhere.example", forwardedFor: "10.1.1.1", basic: ("rl-fwd", "rl-fwd-secret"));
        Assert.NotEqual(HttpStatusCode.TooManyRequests, a1.StatusCode);
        var b1 = await SendAsync(anon, null, "b1@nowhere.example", forwardedFor: "10.1.1.2", basic: ("rl-fwd", "rl-fwd-secret"));
        Assert.NotEqual(HttpStatusCode.TooManyRequests, b1.StatusCode);
        var a2 = await SendAsync(anon, null, "a2@nowhere.example", forwardedFor: "10.1.1.1", basic: ("rl-fwd", "rl-fwd-secret"));
        Assert.Equal(HttpStatusCode.TooManyRequests, a2.StatusCode);
        Assert.Equal("source", (await a2.Content.ReadFromJsonAsync<Limited>(JsonOptions, ct))!.Dimension);
    }

    [Fact]
    public async Task A_forwarder_cannot_exceed_its_client_ceiling()
    {
        var ct = TestContext.Current.CancellationToken;
        await CreateConfidentialClientAsync("rl-fwd-cap", capabilities: [OAuthPermissions.Capabilities.TrustedForwarder]);
        await PatchAsync(Policy("native-otp", source: null, client: Rule(2, 60)), mode: RateLimitEnforcementMode.Enforce);
        var anon = Factory.CreateClient();

        for (var i = 1; i <= 2; i++)
        {
            var ok = await SendAsync(anon, null, $"cap{i}@nowhere.example", forwardedFor: $"10.2.2.{i}", basic: ("rl-fwd-cap", "rl-fwd-cap-secret"));
            Assert.NotEqual(HttpStatusCode.TooManyRequests, ok.StatusCode);
        }
        var over = await SendAsync(anon, null, "cap3@nowhere.example", forwardedFor: "10.2.2.3", basic: ("rl-fwd-cap", "rl-fwd-cap-secret"));
        Assert.Equal(HttpStatusCode.TooManyRequests, over.StatusCode);
        Assert.Equal("client", (await over.Content.ReadFromJsonAsync<Limited>(JsonOptions, ct))!.Dimension);
    }

    [Fact]
    public async Task An_allowlisted_source_skips_the_source_ceiling_but_not_the_target()
    {
        var ct = TestContext.Current.CancellationToken;
        await CreateConfidentialClientAsync("rl-fwd-allow", capabilities: [OAuthPermissions.Capabilities.TrustedForwarder]);
        await PatchAsync(Policy("native-otp", source: Rule(1, 60), target: Rule(2, 60)),
            mode: RateLimitEnforcementMode.Enforce, allowlist: ["10.50.0.0/16"]);
        var anon = Factory.CreateClient();
        var office = ("rl-fwd-allow", "rl-fwd-allow-secret");

        for (var i = 0; i < 3; i++)
        {
            var ok = await SendAsync(anon, null, $"office{i}@nowhere.example", forwardedFor: "10.50.7.7", basic: office);
            Assert.NotEqual(HttpStatusCode.TooManyRequests, ok.StatusCode);
        }
        Assert.NotEqual(HttpStatusCode.TooManyRequests, (await SendAsync(anon, null, "same-office@nowhere.example", forwardedFor: "10.50.7.7", basic: office)).StatusCode);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, (await SendAsync(anon, null, "same-office@nowhere.example", forwardedFor: "10.50.7.7", basic: office)).StatusCode);
        var third = await SendAsync(anon, null, "same-office@nowhere.example", forwardedFor: "10.50.7.7", basic: office);
        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        Assert.Equal("target", (await third.Content.ReadFromJsonAsync<Limited>(JsonOptions, ct))!.Dimension);
    }

    [Fact]
    public async Task Address_spraying_from_one_source_goes_silent_while_the_response_stays_uniform()
    {
        var ct = TestContext.Current.CancellationToken;
        const string host = "rl-spray.localhost";
        await EnableRealmNativeGrantsAsync();
        var app = await CreateAppAsync("rl-spray-app");
        await MapApplicationDomainsAsync((host, app.Id));
        await PatchAsync(Policy("native-otp", source: null, sourceRegistration: Rule(2, 60)), mode: RateLimitEnforcementMode.Enforce);

        var emailService = Factory.Services.GetRequiredService<InMemoryEmailService>();
        emailService.Clear();
        var bodies = new List<string>();
        for (var i = 1; i <= 3; i++)
        {
            var resp = await SendAsync(Client, "spray", $"spray{i}@nowhere.example", host: host);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            bodies.Add(await resp.Content.ReadAsStringAsync(ct));
        }
        Assert.Equal(bodies[0], bodies[2]); // uniform: the throttled request looks like the others
        Assert.NotNull(emailService.GetLastEmailTo("spray1@nowhere.example"));
        Assert.NotNull(emailService.GetLastEmailTo("spray2@nowhere.example"));
        Assert.Null(emailService.GetLastEmailTo("spray3@nowhere.example"));
        Assert.Null(await LoadPendingAsync("spray3@nowhere.example"));
    }

    [Fact]
    public async Task Two_store_instances_on_one_database_agree_on_the_count()
    {
        var ct = TestContext.Current.CancellationToken;
        var connections = Factory.Services.GetRequiredService<IRateLimitConnectionSource>();
        var a = new PostgresRateLimitStore(connections);
        var b = new PostgresRateLimitStore(connections);
        var scope = new RateLimitScope("system");
        var key = $"test|two-stores|{Guid.NewGuid():N}";
        var rule = RateLimitRule.Fixed(3, 60);
        var now = DateTimeOffset.UtcNow;

        Assert.True((await a.HitAsync(scope, key, rule, now, ct)).Allowed);
        Assert.True((await b.HitAsync(scope, key, rule, now, ct)).Allowed);
        Assert.True((await a.HitAsync(scope, key, rule, now, ct)).Allowed);
        var fourth = await b.HitAsync(scope, key, rule, now, ct);
        Assert.False(fourth.Allowed);
        Assert.True(fourth.RetryAfterSeconds > 0);

        // Token bucket: burst, then refill after the store's own clock moved on.
        var bucketKey = $"test|bucket|{Guid.NewGuid():N}";
        var bucket = RateLimitRule.Bucket(60, 1, 2);
        Assert.True((await a.HitAsync(scope, bucketKey, bucket, now, ct)).Allowed);
        Assert.True((await b.HitAsync(scope, bucketKey, bucket, now, ct)).Allowed);
        Assert.False((await a.HitAsync(scope, bucketKey, bucket, now, ct)).Allowed);
        Assert.True((await b.HitAsync(scope, bucketKey, bucket, now.AddSeconds(2), ct)).Allowed);

        Assert.True(await a.PruneAsync(scope, DateTimeOffset.UtcNow.AddMinutes(5), ct) >= 2);
    }

    [Fact]
    public async Task Realm_settings_round_trip_only_stores_overrides_and_resets_with_explicit_null()
    {
        var ct = TestContext.Current.CancellationToken;
        await PatchAsync(Policy("magic-link", source: Rule(7, 30, burst: 3)), mode: RateLimitEnforcementMode.Enforce, allowlist: ["192.0.2.0/24"]);

        var dto = await ReadAsync();
        Assert.Equal(RateLimitEnforcementMode.Enforce, dto.Mode);
        Assert.Equal(["192.0.2.0/24"], dto.SourceAllowlist);
        Assert.False(dto.LegacyOverridesPresent);
        var magic = dto.Policies["magic-link"];
        Assert.Equal((7, 30, 3), (magic.Source!.PermitLimit, magic.Source.WindowMinutes, magic.Source.Burst));
        Assert.Equal(AuthRateLimitDefaults.For(AuthRateLimitPolicy.MagicLink, RateLimitDimension.Target)!.PermitLimit, magic.Target!.PermitLimit);
        Assert.Null(magic.SourceRegistration); // does not apply to magic-link
        Assert.NotNull(dto.Overrides);
        Assert.Single(dto.Overrides!.Policies!);
        Assert.True(dto.Overrides.Policies!["magic-link"]!.Source.HasValue);
        Assert.False(dto.Overrides.Policies["magic-link"]!.Target.HasValue); // never stored

        // A dimension that does not apply is refused.
        var bad = await TryPatchAsync(new UpdateAuthRateLimitsDto
        {
            Policies = new() { ["magic-link"] = new UpdatePolicyLimitsDto { SourceRegistration = new Optional<RateLimitRuleDto?>(Rule(1, 1)) } },
        });
        Assert.True(bad.IsError);

        // Explicit null resets to the default and the override disappears.
        await TryPatchAsync(new UpdateAuthRateLimitsDto
        {
            Policies = new() { ["magic-link"] = new UpdatePolicyLimitsDto { Source = new Optional<RateLimitRuleDto?>(null) } },
            SourceAllowlist = new Optional<string[]?>(null),
        });
        dto = await ReadAsync();
        Assert.Equal(AuthRateLimitDefaults.For(AuthRateLimitPolicy.MagicLink, RateLimitDimension.Source)!.PermitLimit, dto.Policies["magic-link"].Source!.PermitLimit);
        Assert.Empty(dto.SourceAllowlist);
        Assert.True(dto.Overrides?.Policies is null || !dto.Overrides.Policies.ContainsKey("magic-link"));
    }

    [Fact]
    public async Task Legacy_per_ip_rule_is_accepted_but_puts_the_realm_in_log_only_until_a_mode_is_chosen()
    {
        await TryPatchAsync(new UpdateAuthRateLimitsDto { NativeOtp = Rule(1, 60) });
        var dto = await ReadAsync();
        Assert.True(dto.LegacyOverridesPresent);
        Assert.Equal(RateLimitEnforcementMode.LogOnly, dto.Mode);
        // Not migrated into the source ceiling.
        Assert.Equal(AuthRateLimitDefaults.For(AuthRateLimitPolicy.NativeOtp, RateLimitDimension.Source)!.PermitLimit, dto.Policies["native-otp"].Source!.PermitLimit);

        await TryPatchAsync(new UpdateAuthRateLimitsDto { ClearLegacy = true, Mode = new Optional<RateLimitEnforcementMode?>(RateLimitEnforcementMode.Enforce) });
        dto = await ReadAsync();
        Assert.False(dto.LegacyOverridesPresent);
        Assert.Equal(RateLimitEnforcementMode.Enforce, dto.Mode);
    }

    [Fact]
    public async Task Client_capabilities_are_admin_managed_and_confidential_only()
    {
        var ct = TestContext.Current.CancellationToken;
        using var scope = Factory.Services.CreateScope();
        var admin = scope.ServiceProvider.GetRequiredService<OAuthAdminService>();

        var created = await admin.CreateClientAsync(ClientDto("rl-cap-ok", OAuthClientTypes.Confidential, [OAuthPermissions.Capabilities.TrustedForwarder]), ct);
        Assert.False(created.IsError, created.IsError ? created.FirstError.Description : "");
        var read = await admin.GetClientByIdAsync(created.Value.Client.Id, ct);
        Assert.Contains(OAuthPermissions.Capabilities.TrustedForwarder, read!.Capabilities);
        Assert.Contains(OAuthPermissions.GrantTypes.CocoarOtp, read.AllowedGrantTypes.Select(g => OAuthPermissions.Prefixes.GrantType + g));

        var publicClient = await admin.CreateClientAsync(ClientDto("rl-cap-public", OAuthClientTypes.Public, [OAuthPermissions.Capabilities.TrustedForwarder]) with { ClientSecret = null }, ct);
        Assert.True(publicClient.IsError);
        Assert.Equal("OAuthClient.CapabilityRequiresConfidential", publicClient.FirstError.Code);

        var unknown = await admin.CreateClientAsync(ClientDto("rl-cap-unknown", OAuthClientTypes.Confidential, ["cap:nope"]), ct);
        Assert.True(unknown.IsError);
        Assert.Equal("OAuthClient.UnknownCapability", unknown.FirstError.Code);

        // Update replaces the list; grants and scopes survive the rebuild.
        var updated = await admin.UpdateClientAsync(created.Value.Client.Id, new UpdateOAuthClientDto { Capabilities = [] }, ct);
        Assert.False(updated.IsError);
        Assert.Empty(updated.Value.Capabilities);
        Assert.Contains(CocoarGrantTypes.Otp, updated.Value.AllowedGrantTypes);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static RateLimitRuleDto Rule(int limit, int window, int? burst = null) =>
        new() { PermitLimit = limit, WindowMinutes = window, Burst = burst };

    private static UpdateAuthRateLimitsDto Policy(string name,
        RateLimitRuleDto? source = null, RateLimitRuleDto? target = null, RateLimitRuleDto? client = null,
        RateLimitRuleDto? app = null, RateLimitRuleDto? sourceRegistration = null) => new()
    {
        Policies = new()
        {
            [name] = new UpdatePolicyLimitsDto
            {
                Source = new Optional<RateLimitRuleDto?>(source),
                Target = target is null ? default : new Optional<RateLimitRuleDto?>(target),
                Client = client is null ? default : new Optional<RateLimitRuleDto?>(client),
                App = app is null ? default : new Optional<RateLimitRuleDto?>(app),
                SourceRegistration = sourceRegistration is null ? default : new Optional<RateLimitRuleDto?>(sourceRegistration),
            },
        },
    };

    private async Task PatchAsync(UpdateAuthRateLimitsDto limits, RateLimitEnforcementMode mode, string[]? allowlist = null)
    {
        var result = await TryPatchAsync(limits with
        {
            Mode = new Optional<RateLimitEnforcementMode?>(mode),
            SourceAllowlist = new Optional<string[]?>(allowlist),
            ClearLegacy = true,
        });
        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : "");
    }

    private async Task<ErrorOr.ErrorOr<RealmSettingsDto>> TryPatchAsync(UpdateAuthRateLimitsDto limits)
    {
        using var scope = CreateTenantScope();
        var settings = scope.ServiceProvider.GetRequiredService<IRealmSettingsService>();
        return await settings.PatchAsync(new UpdateRealmSettingsDto { AuthRateLimits = limits }, TestContext.Current.CancellationToken);
    }

    private async Task<AuthRateLimitsDto> ReadAsync()
    {
        using var scope = CreateTenantScope();
        var settings = scope.ServiceProvider.GetRequiredService<IRealmSettingsService>();
        var doc = await settings.LoadAsync(TestContext.Current.CancellationToken);
        return RealmSettingsService.MapAuthRateLimitsToDto(doc.AuthRateLimits);
    }

    private Task<HttpResponseMessage> SendAsync(HttpClient client, string? budget, string email,
        string? forwardedFor = null, (string Id, string Secret)? basic = null, string? host = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = JsonContent.Create(new { Email = email }) };
        if (budget is not null) req.Headers.Add("X-Test-RateLimit", $"auth-rl-{budget}");
        if (forwardedFor is not null) req.Headers.Add(AuthCallerContext.ForwardedForHeader, forwardedFor);
        if (basic is { } b)
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Uri.EscapeDataString(b.Id)}:{Uri.EscapeDataString(b.Secret)}")));
        if (host is not null) req.Headers.Host = host;
        return client.SendAsync(req, TestContext.Current.CancellationToken);
    }

    private IServiceScope CreateTenantScope()
    {
        var scope = Factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>()
            .HttpContext = new DefaultHttpContext { Items = { ["TenantId"] = "system" } };
        return scope;
    }

    private static CreateOAuthClientDto ClientDto(string clientId, string clientType, List<string> capabilities) => new()
    {
        ClientId = clientId,
        ClientSecret = $"{clientId}-secret",
        ClientType = clientType,
        ConsentType = OAuthConsentTypes.Implicit,
        DisplayName = clientId,
        RedirectUris = ["https://app.example/callback"],
        PostLogoutRedirectUris = [],
        Scopes = ["openid", "email", "profile"],
        AllowedGrantTypes = [CocoarGrantTypes.Otp, "refresh_token"],
        RequireConsent = false,
        AccessTokenType = AccessTokenType.Jwt,
        Capabilities = capabilities,
    };

    private async Task CreateConfidentialClientAsync(string clientId, List<string> capabilities)
    {
        using var scope = Factory.Services.CreateScope();
        var admin = scope.ServiceProvider.GetRequiredService<OAuthAdminService>();
        var result = await admin.CreateClientAsync(ClientDto(clientId, OAuthClientTypes.Confidential, capabilities), TestContext.Current.CancellationToken);
        if (result.IsError)
            throw new InvalidOperationException(string.Join(", ", result.Errors.Select(e => $"{e.Code}: {e.Description}")));
    }

    private async Task<PendingRegistration?> LoadPendingAsync(string email)
    {
        using var scope = CreateTenantScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        return await session.LoadAsync<PendingRegistration>(PendingRegistration.IdFor(email), TestContext.Current.CancellationToken);
    }

    private async Task EnableRealmNativeGrantsAsync()
    {
        using var scope = CreateTenantScope();
        var settings = scope.ServiceProvider.GetRequiredService<IRealmSettingsService>();
        await settings.PatchAsync(new UpdateRealmSettingsDto
        {
            NativeGrants = new UpdateNativeGrantSettingsDto { Enabled = true },
        }, TestContext.Current.CancellationToken);
    }

    private async Task<App> CreateAppAsync(string slug)
    {
        using var scope = Factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var id = Guid.NewGuid();
        session.Events.StartStream<App>(id, new AppCreatedEvent(
            Id: id, Slug: slug, DisplayName: slug, Description: null, Permissions: [], IsSystem: false));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return (await session.LoadAsync<App>(id, TestContext.Current.CancellationToken))!;
    }

    private async Task MapApplicationDomainsAsync(params (string Host, Guid AppId)[] entries)
    {
        var ct = TestContext.Current.CancellationToken;
        var globalStore = Factory.Services.GetRequiredService<IGlobalStore>();
        await using (var session = globalStore.LightweightSession())
        {
            var systemRealm = await session.Query<Realm>().FirstOrDefaultAsync(r => r.Slug == "system", ct);
            Assert.NotNull(systemRealm);
            foreach (var (host, appId) in entries)
                systemRealm!.ApplicationDomains[host] = appId;
            session.Store(systemRealm!);
            await session.SaveChangesAsync(ct);
        }
        Factory.Services.GetRequiredService<IRealmCache>().Invalidate();
    }
}
