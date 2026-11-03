using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Marten;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Authentication.Domain;
using Modgud.Authentication.Sessions;
using Modgud.Authentication.SignIn;
using Modgud.Domain.Realms;
using Modgud.Infrastructure.Persistence.Tenancy;
using Modgud.Infrastructure.Realms;

namespace Modgud.Api.Tests.Security;

/// <summary>
/// 3z9t investigation, NOT a fix: compare the historical CookieContainer
/// transport with Chromium's independently observed old-before-new ordering.
/// Proofs/tickets/keys remain in memory; diagnostics expose only booleans/counts.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class PrimaryDomainCookieScopeTests(SharedPostgresFixture fixture) : IntegrationTestBase(fixture)
{
    private const string OldPrimary = "auth.example.test";
    private const string ProfileHost = "portal.auth.example.test";
    private const string NewPrimary = "changed.example.test";
    private static readonly Uri Origin = new("https://" + ProfileHost);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private sealed record Proof(bool SameOwnerAndSession, bool Recorded, bool RecentPasskey, bool StaleEmailOnly);

    private sealed class ObservedCookies(
        CookieAuthenticationOptions options, Func<string?, Proof> inspect) : DelegatingHandler
    {
        private readonly CookieContainer _cookies = new();
        private Cookie[]? _issuedPair;
        public string Order { get; set; } = "container";
        public Proof? Selected { get; private set; }
        public int ForwardedAuthCount { get; private set; }

        public Cookie[] AuthCookies() => _cookies.GetCookies(Origin).Cast<Cookie>()
            .Where(c => c.Name == options.Cookie.Name).ToArray();

        public void Seed(string value) => _cookies.Add(Origin,
            new Cookie(options.Cookie.Name!, value) { Path = "/", Domain = OldPrimary });

        public void ReplayIssuedPair(IEnumerable<Cookie> cookies) => _issuedPair = cookies
            .Select(c => new Cookie(c.Name, c.Value, c.Path, c.Domain)).ToArray();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            var cookies = _cookies.GetCookies(uri).Cast<Cookie>().ToArray();
            var header = _cookies.GetCookieHeader(uri);
            if (Order != "container")
            {
                // Only the relative order/selection of these two authentication
                // cookies changes. No signatures, ticket contents or server state.
                var auth = (_issuedPair ?? cookies.Where(c => c.Name == options.Cookie.Name).ToArray())
                    .Where(c => Order != "fresh-only" || c.Domain.TrimStart('.') == ProfileHost)
                    .OrderBy(c => c.Domain.TrimStart('.') == ProfileHost ? 1 : 0);
                header = string.Join("; ", cookies.Where(c => c.Name != options.Cookie.Name).Concat(auth)
                    .Select(c => $"{c.Name}={c.Value}"));
            }
            ForwardedAuthCount = header.Split(';').Count(s =>
                s.TrimStart().StartsWith(options.Cookie.Name + "=", StringComparison.Ordinal));
            var mirror = new DefaultHttpContext();
            mirror.Request.Headers.Cookie = header;
            // Same CookieManager and ASP.NET request-cookie parser as the server.
            Selected = inspect(options.CookieManager.GetRequestCookie(mirror, options.Cookie.Name!));
            if (!string.IsNullOrEmpty(header)) request.Headers.Add("Cookie", header);
            var response = await base.SendAsync(request, ct);
            if (response.Headers.TryGetValues("Set-Cookie", out var values))
                foreach (var value in values) _cookies.SetCookies(uri, value);
            return response;
        }
    }

    [Theory]
    [InlineData(false, "container", true)]
    [InlineData(true, "container", false)]
    [InlineData(true, "chromium-relative-order", true)]
    [InlineData(true, "fresh-only", true)]
    public async Task Domain_switch_separates_successful_proof_from_transport_ticket_selection(
        bool switchPrimary, string order, bool expectRecent)
    {
        try
        {
            await SetPrimaryAsync(OldPrimary);
            using var authenticator = new SoftwareWebAuthnAuthenticator(DefaultUser!.Id.ToByteArray());
            var credentialId = Guid.NewGuid();
            await using (var session = GetTenantedDocumentSession())
            {
                session.Store(new StoredPasskeyCredential
                {
                    Id = credentialId, UserId = DefaultUser.Id, RpId = null,
                    CredentialId = authenticator.CredentialId, PublicKey = authenticator.CosePublicKey(),
                    UserHandle = authenticator.UserHandle, DisplayName = "Synthetic domain-switch proof",
                });
                await session.SaveChangesAsync(Ct);
            }

            using var scope = Factory.Services.CreateScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var signIn = scope.ServiceProvider.GetRequiredService<SignInManager<ApplicationUser>>();
            var user = await users.FindByIdAsync(DefaultUser.Id.ToString());
            Assert.NotNull(user);
            var principal = await signIn.CreateUserPrincipalAsync(user);
            var identity = (ClaimsIdentity)principal.Identity!;
            SignInAssurance.Stamp(identity, new Dictionary<string, DateTimeOffset>
            {
                [SignInMethods.Email] = DateTimeOffset.UtcNow.AddMinutes(-30),
            });
            SignInAssurance.MarkRecorded(identity);
            var created = await scope.ServiceProvider.GetRequiredService<ISessionService>()
                .CreateSessionAsync(user.Id, null, nameof(PrimaryDomainCookieScopeTests), Ct);
            Assert.False(created.IsError);
            identity.AddClaim(new Claim(SessionClaimTypes.BrowserSessionId, created.Value.Id.ToString()));
            var cookieOptions = scope.ServiceProvider.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
                .Get(IdentityConstants.ApplicationScheme);
            using var cookies = new ObservedCookies(cookieOptions, Inspect);
            using (TenantContext.Enter(TenantConstants.SystemTenantId))
                cookies.Seed(cookieOptions.TicketDataFormat.Protect(new AuthenticationTicket(
                    principal, new AuthenticationProperties
                    {
                        IssuedUtc = DateTimeOffset.UtcNow.AddMinutes(-30),
                        ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1),
                    }, IdentityConstants.ApplicationScheme)));
            using var client = Factory.CreateDefaultClient(cookies);
            client.BaseAddress = Origin;

            using (var refusal = await client.DeleteAsync($"/api/account/passkey/{credentialId}", Ct))
            {
                Assert.Equal(HttpStatusCode.Forbidden, refusal.StatusCode);
                var body = await refusal.Content.ReadFromJsonAsync<JsonElement>(Ct);
                Assert.True(body.GetProperty("RequiresReauthentication").GetBoolean());
            }
            using (var step = await client.PostAsJsonAsync("/api/account/step-up",
                new { ReturnUrl = "/profile", Reauthenticate = true }, Ct))
            {
                Assert.Equal(HttpStatusCode.OK, step.StatusCode);
                var body = await step.Content.ReadFromJsonAsync<JsonElement>(Ct);
                Assert.Equal(["passkey"], body.GetProperty("MfaMethods").EnumerateArray().Select(m => m.GetString()));
            }
            using var begin = await client.PostAsJsonAsync("/api/account/passkey/login-options",
                new { ReturnUrl = "/profile" }, Ct);
            Assert.Equal(HttpStatusCode.OK, begin.StatusCode);
            var options = await begin.Content.ReadFromJsonAsync<JsonElement>(Ct);
            Assert.Equal(OldPrimary, options.GetProperty("rpId").GetString());
            if (switchPrimary) await SetPrimaryAsync(NewPrimary);
            var currentRealm = await Factory.Services.GetRequiredService<IRealmCache>().ResolveAsync(ProfileHost);
            Assert.Equal(switchPrimary ? NewPrimary : OldPrimary, currentRealm!.Tenant.PrimaryDomain);

            using var assertion = await client.PostAsync("/api/account/passkey/login",
                new StringContent(authenticator.CreateAssertionJson(
                    options.GetProperty("challenge").GetString()!, OldPrimary, Origin.GetLeftPart(UriPartial.Authority)),
                    Encoding.UTF8, "application/json"), Ct);
            Assert.Equal(HttpStatusCode.OK, assertion.StatusCode);
            var stored = await Factory.GetDocumentAsync<StoredPasskeyCredential>(credentialId);
            Assert.NotNull(stored);
            Assert.Equal(1u, stored.SignatureCount);
            Assert.True(stored.LastUsedAt.HasValue);

            var authCookies = cookies.AuthCookies();
            Assert.Equal(switchPrimary ? 2 : 1, authCookies.Length);
            Assert.All(authCookies, c =>
            {
                var proof = Inspect(c.Value);
                Assert.True(proof.SameOwnerAndSession);
                Assert.True(proof.Recorded);
            });
            var parent = Inspect(authCookies.Single(c => c.Domain.TrimStart('.') == OldPrimary).Value);
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"Synthetic switch={switchPrimary}; parentRecent={parent.RecentPasskey}; parentStaleEmail={parent.StaleEmailOnly}");
            Assert.Equal(!switchPrimary, parent.RecentPasskey);
            Assert.Equal(switchPrimary, parent.StaleEmailOnly);
            if (switchPrimary)
                Assert.True(Inspect(authCookies.Single(c => c.Domain.TrimStart('.') == ProfileHost).Value).RecentPasskey);

            cookies.Order = order;
            // No business mutation; the fixture's zero stamp interval CAN renew
            // auth cookies while confirming the factor view over actual HTTP.
            using var check = await client.PostAsJsonAsync("/api/account/step-up",
                new { ReturnUrl = "/profile", Reauthenticate = true }, Ct);
            Assert.Equal(HttpStatusCode.OK, check.StatusCode);
            var checkBody = await check.Content.ReadFromJsonAsync<JsonElement>(Ct);
            Assert.True(expectRecent
                ? checkBody.TryGetProperty("Message", out _)
                : checkBody.TryGetProperty("RequiresFirstFactor", out var first) && first.GetBoolean());
            Assert.NotNull(cookies.Selected);
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"Synthetic order={order}; selectedRecent={cookies.Selected.RecentPasskey}; " +
                $"selectedStaleEmail={cookies.Selected.StaleEmailOnly}; forwardedAuthCount={cookies.ForwardedAuthCount}");
            Assert.True(cookies.Selected.SameOwnerAndSession);
            Assert.Equal(expectRecent, cookies.Selected.RecentPasskey);
            Assert.Equal(!expectRecent, cookies.Selected.StaleEmailOnly);
            var forwardedCount = cookies.ForwardedAuthCount;
            Assert.Equal(switchPrimary && order != "fresh-only" ? 2 : 1, forwardedCount);

            using var mutation = await client.DeleteAsync($"/api/account/passkey/{credentialId}", Ct);
            Assert.Equal(expectRecent ? HttpStatusCode.OK : HttpStatusCode.Forbidden, mutation.StatusCode);
            if (!expectRecent)
            {
                var body = await mutation.Content.ReadFromJsonAsync<JsonElement>(Ct);
                Assert.True(body.GetProperty("RequiresReauthentication").GetBoolean());
                // The fixture validates stamps on EVERY request. Having selected
                // the stale principal, it renewed that into the new host scope.
                Assert.True(Inspect(cookies.AuthCookies()
                    .Single(c => c.Domain.TrimStart('.') == ProfileHost).Value).StaleEmailOnly);
                Assert.True(Inspect(authCookies.Single(c => c.Domain.TrimStart('.') == ProfileHost).Value).RecentPasskey);
                // Causal intervention on the originally issued pair, not the jar
                // overwritten by that renewal. No new assertion or ticket mint.
                cookies.ReplayIssuedPair(authCookies);
                cookies.Order = "chromium-relative-order";
                using var retry = await client.DeleteAsync($"/api/account/passkey/{credentialId}", Ct);
                Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
                Assert.True(cookies.Selected!.RecentPasskey);
            }
            Assert.Null(await Factory.GetDocumentAsync<StoredPasskeyCredential>(credentialId));
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"Synthetic switch={switchPrimary}; order={order}; storedProofVerified=True; " +
                $"jarAuthCount={authCookies.Length}; forwardedAuthCount={forwardedCount}; selectedRecent={expectRecent}");

            Proof Inspect(string? value)
            {
                using var tenant = TenantContext.Enter(TenantConstants.SystemTenantId);
                var ticket = value is null ? null : cookieOptions.TicketDataFormat.Unprotect(value);
                var factors = SignInAssurance.ReadFactors(ticket?.Principal);
                var since = DateTimeOffset.UtcNow - Modgud.Authentication.Api.Account.AccountChangeGuard.Window;
                return new Proof(
                    ticket?.Principal.FindFirstValue(ClaimTypes.NameIdentifier) == user.Id.ToString()
                        && ticket.Principal.FindFirstValue(SessionClaimTypes.BrowserSessionId) == created.Value.Id.ToString(),
                    SignInAssurance.IsRecorded(ticket?.Principal),
                    factors.TryGetValue(SignInMethods.Passkey, out var at) && at >= since,
                    factors.Count == 1 && factors.TryGetValue(SignInMethods.Email, out var emailAt) && emailAt < since);
            }
        }
        finally
        {
            // Global metadata is not part of the tenant-data fixture reset.
            await SetPrimaryAsync("localhost");
        }
    }

    private async Task SetPrimaryAsync(string primary)
    {
        await using var session = Factory.Services.GetRequiredService<IGlobalStore>().LightweightSession();
        var realm = await session.Query<Realm>().SingleAsync(r => r.Slug == "system", Ct);
        if (!realm.Domains.Contains(ProfileHost)) realm.Domains = [.. realm.Domains, ProfileHost];
        realm.PrimaryDomain = primary;
        session.Store(realm);
        await session.SaveChangesAsync(Ct);
        Factory.Services.GetRequiredService<IRealmCache>().Invalidate();
    }
}
