using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using BuildingBlocks.Helper;
using Marten;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Features.Admin.Jobs;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Application.DTOs.OAuth;
using Modgud.Application.DTOs.Realms;
using Modgud.Application.DTOs.RealmSettings;
using Modgud.Application.DTOs.SelfRegistration;
using Modgud.Application.Services;
using Modgud.Authentication.Domain;
using Modgud.Authentication.RealmSettings;
using Modgud.Authentication.Registration;
using Modgud.Authorization.Apps;
using Modgud.Authorization.Events;
using Modgud.Domain.OAuth.Common;
using Modgud.Domain.Realms;
using Modgud.Infrastructure.Email;
using Modgud.Infrastructure.Persistence.Tenancy;
using Modgud.Infrastructure.Realms;

namespace Modgud.Api.Tests.Authorization;

/// <summary>
/// ADR 0006 — registration before proof. One <see cref="PendingRegistration"/> per address
/// for every sign-up path; the user is created exactly once when the proof succeeds; a
/// pending record is a hard-deletable document that never blocks the real owner.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class RegistrationPipelineTests : IntegrationTestBase
{
    public RegistrationPipelineTests(SharedPostgresFixture fixture) : base(fixture) { }

    private const string Password = "Str0ngPassw0rd!";

    // ── Native (code proof) ──────────────────────────────────────────────────

    [Fact]
    public async Task Wrong_codes_create_nothing_and_burn_the_pending_after_the_attempt_cap()
    {
        var ct = TestContext.Current.CancellationToken;
        const string host = "rp-wrong.localhost";
        const string email = "rp-wrong@example.test";
        await SetUpJitAppAsync("rp-wrong", host);

        var mail = await RequestCodeAsync(host, email);
        var code = ExtractCode(mail);
        var wrong = code == "000000" ? "111111" : "000000";

        for (var i = 0; i < RegistrationPipeline.CodeMaxAttempts; i++)
        {
            var resp = await PostTokenAsync(host, "rp-wrong-client", email, wrong);
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        }
        Assert.Null(await QueryUserByEmailAsync(email));

        // The cap burned the record: even the right code proves nothing now.
        var late = await PostTokenAsync(host, "rp-wrong-client", email, code);
        Assert.Equal(HttpStatusCode.BadRequest, late.StatusCode);
        Assert.Null(await QueryUserByEmailAsync(email));
        Assert.Null(await LoadPendingAsync(email));
    }

    [Fact]
    public async Task Expired_pending_is_swept_and_cannot_be_proved()
    {
        var ct = TestContext.Current.CancellationToken;
        const string host = "rp-expired.localhost";
        const string email = "rp-expired@example.test";
        await SetUpJitAppAsync("rp-expired", host);

        var code = ExtractCode(await RequestCodeAsync(host, email));
        await MutatePendingAsync(email, p => p.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1));

        int swept;
        using (var scope = CreateTenantScope())
            swept = await scope.ServiceProvider.GetRequiredService<IRegistrationPipeline>().SweepAsync(ct);
        Assert.True(swept >= 1);
        Assert.Null(await LoadPendingAsync(email));

        var resp = await PostTokenAsync(host, "rp-expired-client", email, code);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Null(await QueryUserByEmailAsync(email));
    }

    [Fact]
    public async Task Concurrent_proofs_of_one_pending_create_exactly_one_user()
    {
        var ct = TestContext.Current.CancellationToken;
        const string host = "rp-race.localhost";
        const string email = "rp-race@example.test";
        await SetUpJitAppAsync("rp-race", host);

        var code = ExtractCode(await RequestCodeAsync(host, email));

        var a = PostTokenAsync(host, "rp-race-client", email, code);
        var b = PostTokenAsync(host, "rp-race-client", email, code);
        await Task.WhenAll(a, b);

        var successes = new[] { (await a).StatusCode, (await b).StatusCode }.Count(s => s == HttpStatusCode.OK);
        Assert.Equal(1, successes);

        var users = await QueryUsersByEmailAsync(email);
        Assert.Single(users);
        Assert.True(users[0].EmailConfirmed);
        Assert.Null(await LoadPendingAsync(email)); // consumed → hard-deleted, nothing left
    }

    [Fact]
    public async Task A_strangers_pending_never_blocks_the_owner()
    {
        var ct = TestContext.Current.CancellationToken;
        const string host = "rp-owner.localhost";
        const string email = "rp-owner@example.test";
        await SetUpJitAppAsync("rp-owner", host);

        // A stranger types the owner's address.
        var strangerCode = ExtractCode(await RequestCodeAsync(host, email, firstName: "Stranger"));
        // Past the cooldown the owner asks for a code themselves.
        await MutatePendingAsync(email, p => p.LastSentAt = DateTimeOffset.UtcNow - RegistrationPipeline.ResendCooldown - TimeSpan.FromMinutes(1));
        var ownerCode = ExtractCode(await RequestCodeAsync(host, email, firstName: "Owner"));
        Assert.NotEqual(strangerCode, ownerCode);

        // The stranger's code is dead; the owner's proves and creates the OWNER's account.
        Assert.Equal(HttpStatusCode.BadRequest, (await PostTokenAsync(host, "rp-owner-client", email, strangerCode)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostTokenAsync(host, "rp-owner-client", email, ownerCode)).StatusCode);

        var user = await QueryUserByEmailAsync(email);
        Assert.NotNull(user);
        Assert.Equal("Owner", user!.Firstname);
        Assert.Equal(RegistrationSources.NativeJit, user.RegistrationSource);
        Assert.NotNull(user.RegisteredAt);
    }

    [Fact]
    public async Task Second_request_within_the_cooldown_sends_nothing_and_keeps_one_record()
    {
        var ct = TestContext.Current.CancellationToken;
        const string host = "rp-cooldown.localhost";
        const string email = "rp-cooldown@example.test";
        await SetUpJitAppAsync("rp-cooldown", host);

        var emailService = Factory.Services.GetRequiredService<InMemoryEmailService>();
        emailService.Clear();
        (await RequestNativeOtpAsync(host, email)).EnsureSuccessStatusCode();
        (await RequestNativeOtpAsync(host, email)).EnsureSuccessStatusCode();

        Assert.Equal(1, emailService.GetSentEmails().Count(m => m.To == email));
        var pending = await LoadPendingAsync(email);
        Assert.NotNull(pending);
        Assert.Equal(1, pending!.SendCount);
        Assert.Null(await QueryUserByEmailAsync(email));
    }

    // ── Web (link proof) ─────────────────────────────────────────────────────

    [Fact]
    public async Task Web_self_registration_creates_no_user_until_the_link_is_proved()
    {
        var ct = TestContext.Current.CancellationToken;
        const string email = "rp-web@example.test";
        await SetSelfRegistrationAsync(enabled: true, requireEmailVerification: true);

        var emailService = Factory.Services.GetRequiredService<InMemoryEmailService>();
        emailService.Clear();
        var register = await Client.PostAsJsonAsync("/api/account/register",
            new RegisterDto { UserName = "rpweb", Email = email, Password = Password, Firstname = "Web", Lastname = "User", AcceptedTerms = true },
            JsonOptions, ct);
        register.EnsureSuccessStatusCode();

        // Nothing but a pending record exists.
        Assert.Null(await QueryUserByEmailAsync(email));
        var pending = await LoadPendingAsync(email);
        Assert.NotNull(pending);
        Assert.Equal(RegistrationProofKind.Link, pending!.ProofKind);
        Assert.False(string.IsNullOrEmpty(pending.PasswordHash));

        var mail = emailService.GetLastEmailTo(email);
        Assert.NotNull(mail);
        var token = Uri.UnescapeDataString(Regex.Match(mail!.HtmlBody, @"token=([A-Za-z0-9_\-%]+)").Groups[1].Value);
        Assert.False(string.IsNullOrEmpty(token));

        var verify = await Client.PostAsJsonAsync("/api/account/register/verify-email", new { Token = token }, JsonOptions, ct);
        var verifyBody = await verify.Content.ReadAsStringAsync(ct);
        Assert.True(verify.IsSuccessStatusCode, verifyBody);

        var user = await QueryUserByEmailAsync(email);
        Assert.NotNull(user);
        Assert.True(user!.EmailConfirmed);
        Assert.True(user.IsActive);
        Assert.Equal("rpweb", user.UserName);
        Assert.Equal("Web", user.Firstname);
        Assert.Equal(RegistrationSources.Web, user.RegistrationSource);
        Assert.Null(await LoadPendingAsync(email));

        // The password hashed at request time is the one that works.
        using var authed = await CreateAuthenticatedClientAsync("rpweb", Password);

        // The link is single-use.
        var again = await Client.PostAsJsonAsync("/api/account/register/verify-email", new { Token = token }, JsonOptions, ct);
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
    }

    [Fact]
    public async Task Web_self_registration_without_verification_creates_the_user_immediately()
    {
        var ct = TestContext.Current.CancellationToken;
        const string email = "rp-web-noverify@example.test";
        await SetSelfRegistrationAsync(enabled: true, requireEmailVerification: false);

        var emailService = Factory.Services.GetRequiredService<InMemoryEmailService>();
        emailService.Clear();
        var register = await Client.PostAsJsonAsync("/api/account/register",
            new RegisterDto { UserName = "rpnoverify", Email = email, Password = Password, AcceptedTerms = true },
            JsonOptions, ct);
        register.EnsureSuccessStatusCode();

        var user = await QueryUserByEmailAsync(email);
        Assert.NotNull(user);
        Assert.True(user!.EmailConfirmed);
        Assert.Equal(RegistrationSources.Web, user.RegistrationSource);
        Assert.Null(await LoadPendingAsync(email));
        Assert.Null(emailService.GetLastEmailTo(email));
    }

    // ── Legacy clean-up ──────────────────────────────────────────────────────

    [Fact]
    public async Task Reaper_erases_only_legacy_ghosts_and_only_when_not_dry_run()
    {
        var ct = TestContext.Current.CancellationToken;
        const string ghostEmail = "rp-ghost@example.test";
        const string keepEmail = "rp-keep-password@example.test";

        Guid ghostId, keepId;
        using (var scope = CreateTenantScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

            // The old JIT path: passwordless, unconfirmed, code issued, never redeemed.
            var ghost = new ApplicationUser(ghostEmail, ghostEmail) { IsActive = true };
            Assert.True((await userManager.CreateAsync(ghost)).Succeeded);
            ghostId = ghost.Id;
            session.Store(new EmailOtpChallenge
            {
                Id = ghost.Id, CodeHash = "dead", Email = ghostEmail,
                CreatedAt = DateTimeOffset.UtcNow.AddDays(-10), ExpiresAt = DateTimeOffset.UtcNow.AddDays(-10),
            });
            await session.SaveChangesAsync(ct);

            // Unconfirmed but WITH a password → outside the signature, must survive.
            var keep = new ApplicationUser("rp-keep-password", keepEmail) { IsActive = true };
            Assert.True((await userManager.CreateAsync(keep, Password)).Succeeded);
            keepId = keep.Id;
        }

        using (var scope = CreateTenantScope())
        {
            var job = ActivatorUtilities.CreateInstance<UnconfirmedRegistrationReaperJob>(scope.ServiceProvider);
            var dry = await job.RunAsync(dryRun: true, olderThanDays: 0, ct);
            Assert.Equal(1, dry.Matched);
            Assert.Equal(0, dry.Erased);
        }
        Assert.NotNull(await QueryUserByEmailAsync(ghostEmail)); // dry run touched nothing

        using (var scope = CreateTenantScope())
        {
            var job = ActivatorUtilities.CreateInstance<UnconfirmedRegistrationReaperJob>(scope.ServiceProvider);
            var run = await job.RunAsync(dryRun: false, olderThanDays: 0, ct);
            Assert.Equal((1, 1), run);
        }

        Assert.Null(await QueryUserByEmailAsync(ghostEmail));
        using (var scope = CreateTenantScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var erased = await session.LoadAsync<ApplicationUser>(ghostId, ct);
            Assert.True(erased is null || erased.IsDeleted);
            var kept = await session.LoadAsync<ApplicationUser>(keepId, ct);
            Assert.NotNull(kept);
            Assert.False(kept!.IsDeleted);
            Assert.Equal(keepEmail, kept.Email);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task SetUpJitAppAsync(string slug, string host)
    {
        await EnableRealmNativeGrantsAsync();
        var app = await CreateAppAsync($"{slug}-app"); // no ApplicationSettings doc → posture defaults JitOnOtp
        await CreateOtpClientAsync($"{slug}-client", app.Id);
        await MapApplicationDomainsAsync((host, app.Id));
    }

    private async Task<SentEmail> RequestCodeAsync(string host, string email, string? firstName = null)
    {
        var emailService = Factory.Services.GetRequiredService<InMemoryEmailService>();
        emailService.Clear();
        (await RequestNativeOtpAsync(host, email, firstName)).EnsureSuccessStatusCode();
        var mail = emailService.GetLastEmailTo(email);
        Assert.NotNull(mail);
        return mail!;
    }

    private static string ExtractCode(SentEmail mail)
    {
        var code = Regex.Match(mail.HtmlBody, @"\b(\d{6})\b").Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(code));
        return code;
    }

    private Task<HttpResponseMessage> RequestNativeOtpAsync(string host, string email, string? firstName = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/account/native/otp/request")
        {
            Content = JsonContent.Create(new { Email = email, FirstName = firstName }),
        };
        req.Headers.Host = host;
        return Client.SendAsync(req, TestContext.Current.CancellationToken);
    }

    private Task<HttpResponseMessage> PostTokenAsync(string host, string clientId, string email, string code)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/connect/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = CocoarGrantTypes.Otp,
                ["client_id"] = clientId,
                ["client_secret"] = $"{clientId}-secret",
                ["username"] = email,
                ["otp_code"] = code,
                ["scope"] = "openid email profile offline_access",
            }),
        };
        req.Headers.Host = host;
        return Client.SendAsync(req, TestContext.Current.CancellationToken);
    }

    private IServiceScope CreateTenantScope()
    {
        var scope = Factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>()
            .HttpContext = new DefaultHttpContext { Items = { ["TenantId"] = "system" } };
        return scope;
    }

    private async Task<ApplicationUser?> QueryUserByEmailAsync(string email) =>
        (await QueryUsersByEmailAsync(email)).FirstOrDefault();

    private async Task<List<ApplicationUser>> QueryUsersByEmailAsync(string email)
    {
        using var scope = CreateTenantScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        return (await session.Query<ApplicationUser>()
            .Where(u => u.NormalizedEmail == email.ToUpperInvariant() && !u.IsDeleted)
            .ToListAsync(TestContext.Current.CancellationToken)).ToList();
    }

    private async Task<PendingRegistration?> LoadPendingAsync(string email)
    {
        using var scope = CreateTenantScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        return await session.LoadAsync<PendingRegistration>(PendingRegistration.IdFor(email), TestContext.Current.CancellationToken);
    }

    private async Task MutatePendingAsync(string email, Action<PendingRegistration> mutate)
    {
        using var scope = CreateTenantScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var pending = await session.LoadAsync<PendingRegistration>(PendingRegistration.IdFor(email), TestContext.Current.CancellationToken);
        Assert.NotNull(pending);
        mutate(pending!);
        session.Store(pending!);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task SetSelfRegistrationAsync(bool enabled, bool requireEmailVerification)
    {
        using var scope = CreateTenantScope();
        var settings = scope.ServiceProvider.GetRequiredService<IRealmSettingsService>();
        await settings.PatchAsync(new UpdateRealmSettingsDto
        {
            SelfRegistration = new UpdateSelfRegistrationDto
            {
                Enabled = enabled,
                RequireEmailVerification = requireEmailVerification,
                RequireAdminApproval = false,
            },
        }, TestContext.Current.CancellationToken);
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

    private async Task CreateOtpClientAsync(string clientId, Guid appId)
    {
        using var scope = Factory.Services.CreateScope();
        var oauthAdmin = scope.ServiceProvider.GetRequiredService<OAuthAdminService>();
        var dto = new CreateOAuthClientDto
        {
            ClientId = clientId,
            ClientSecret = $"{clientId}-secret",
            ClientType = OAuthClientTypes.Confidential,
            ConsentType = OAuthConsentTypes.Implicit,
            DisplayName = clientId,
            RedirectUris = ["https://app.example/callback"],
            PostLogoutRedirectUris = [],
            Scopes = ["openid", "email", "profile", "offline_access"],
            AllowedGrantTypes = [CocoarGrantTypes.Otp, "refresh_token"],
            RequireConsent = false,
            AccessTokenType = AccessTokenType.Jwt,
            AppIds = [new ShortGuid(appId).ToString()],
        };
        var result = await oauthAdmin.CreateClientAsync(dto, TestContext.Current.CancellationToken);
        if (result.IsError)
            throw new InvalidOperationException(
                $"CreateClientAsync failed: {string.Join(", ", result.Errors.Select(e => $"{e.Code}: {e.Description}"))}");
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
