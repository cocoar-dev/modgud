using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Application.DTOs.OAuth;
using Modgud.Application.Services;
using Modgud.Authentication.Setup;
using Modgud.Domain.OAuth.Applications;
using Modgud.Domain.OAuth.Common;

namespace Modgud.Api.Tests.Authorization;

/// <summary>
/// Verifies the one-time PAR-permission boot backfill
/// (<see cref="PushedAuthorizationPermissionBackfill"/>): existing OAuth clients
/// that predate the <c>ept:pushed_authorization</c> baseline get it added on the
/// next boot, without a manual re-save and without touching anything else — in
/// particular the separate <c>ft:par</c> requirement.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class PushedAuthorizationPermissionBackfillTests : IntegrationTestBase
{
    public PushedAuthorizationPermissionBackfillTests(SharedPostgresFixture fixture) : base(fixture) { }

    private const string Par = OAuthPermissions.Endpoints.PushedAuthorization;
    private const string ParRequirement = OAuthPermissions.Requirements.PushedAuthorizationRequests;

    [Fact]
    public async Task Backfill_adds_par_permission_preserves_the_rest_and_is_idempotent()
    {
        var ct = TestContext.Current.CancellationToken;
        var clientId = $"legacy-{Guid.NewGuid():N}";

        // A client that also *requires* PAR (ft:par), so we can prove the
        // requirement is left untouched by the permission backfill.
        await CreateConfidentialClientAsync(clientId, "S3cret!Value", "https://app.example/cb",
            ["openid"], requirePar: true);
        var streamId = await StreamIdForAsync(clientId, ct);

        // Simulate a pre-migration client: strip the PAR endpoint permission.
        var (permsBefore, reqsBefore) = await StripParPermissionAsync(streamId, ct);
        Assert.DoesNotContain(Par, permsBefore);
        Assert.Contains(ParRequirement, reqsBefore); // ft:par is on and must stay on

        var versionBefore = await StreamVersionAsync(streamId, ct);

        // Run the boot migration.
        await RunBackfillAsync(ct);

        var agg = await LoadAggregateAsync(streamId, ct);
        // Exactly the previous permissions plus PAR — nothing else added or dropped.
        Assert.Equal(
            permsBefore.Append(Par).OrderBy(p => p, StringComparer.Ordinal),
            agg.Permissions.OrderBy(p => p, StringComparer.Ordinal));
        // The ft:par requirement (and any others) are byte-for-byte unchanged.
        Assert.Equal(reqsBefore, agg.Requirements);

        // One append happened.
        var versionAfter = await StreamVersionAsync(streamId, ct);
        Assert.Equal(versionBefore + 1, versionAfter);

        // Idempotent: a second boot appends nothing.
        await RunBackfillAsync(ct);
        Assert.Equal(versionAfter, await StreamVersionAsync(streamId, ct));
    }

    [Fact]
    public async Task A_client_that_already_has_the_permission_is_left_untouched()
    {
        var ct = TestContext.Current.CancellationToken;
        var clientId = $"fresh-{Guid.NewGuid():N}";

        // A freshly-created client already carries the PAR permission.
        await CreateConfidentialClientAsync(clientId, "S3cret!Value", "https://app.example/cb", ["openid"]);
        var streamId = await StreamIdForAsync(clientId, ct);
        Assert.Contains(Par, (await LoadAggregateAsync(streamId, ct)).Permissions);

        var versionBefore = await StreamVersionAsync(streamId, ct);
        await RunBackfillAsync(ct);
        Assert.Equal(versionBefore, await StreamVersionAsync(streamId, ct)); // no new event
    }

    [Fact]
    public async Task Backfilled_client_can_reach_the_par_endpoint()
    {
        var ct = TestContext.Current.CancellationToken;
        var clientId = $"legacy-par-{Guid.NewGuid():N}";
        const string secret = "S3cret!Value";

        await CreateConfidentialClientAsync(clientId, secret, "https://app.example/cb", ["openid"]);
        var streamId = await StreamIdForAsync(clientId, ct);
        await StripParPermissionAsync(streamId, ct);

        // Before the backfill the advertised endpoint refuses the client (ID2183).
        Assert.NotEqual(HttpStatusCode.Created, await PostParAsync(clientId, secret));

        await RunBackfillAsync(ct);

        // After the backfill the same client is accepted.
        Assert.Equal(HttpStatusCode.Created, await PostParAsync(clientId, secret));
    }

    // ── helpers ──

    private async Task RunBackfillAsync(CancellationToken ct)
    {
        var migration = Factory.Services.GetServices<IHostedService>()
            .OfType<PushedAuthorizationPermissionBackfill>().Single();
        await migration.StartAsync(ct);
    }

    private async Task<Guid> StreamIdForAsync(string clientId, CancellationToken ct)
    {
        using var session = GetTenantedSession();
        var state = await session.Query<OAuthApplicationState>()
            .FirstOrDefaultAsync(x => x.ClientId == clientId, ct);
        Assert.NotNull(state);
        return state!.Id;
    }

    private async Task<OAuthApplicationAggregate> LoadAggregateAsync(Guid streamId, CancellationToken ct)
    {
        using var session = GetTenantedSession();
        var agg = await session.Events.AggregateStreamAsync<OAuthApplicationAggregate>(streamId, token: ct);
        Assert.NotNull(agg);
        return agg!;
    }

    private async Task<long> StreamVersionAsync(Guid streamId, CancellationToken ct)
    {
        using var session = GetTenantedSession();
        var events = await session.Events.FetchStreamAsync(streamId, token: ct);
        return events.Count == 0 ? 0 : events[^1].Version;
    }

    private async Task<(List<string> perms, List<string> reqs)> StripParPermissionAsync(Guid streamId, CancellationToken ct)
    {
        using var session = GetTenantedDocumentSession();
        var agg = await session.Events.AggregateStreamAsync<OAuthApplicationAggregate>(streamId, token: ct);
        Assert.NotNull(agg);
        var stripped = agg!.Permissions.Where(p => p != Par).ToList();
        session.Events.Append(streamId, agg.SetPermissions(stripped));
        await session.SaveChangesAsync(ct);
        return (stripped, agg.Requirements.ToList());
    }

    private async Task<HttpStatusCode> PostParAsync(string clientId, string clientSecret)
    {
        var verifier = GeneratePkceVerifier();
        var form = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = clientId,
            ["redirect_uri"] = "https://app.example/cb",
            ["scope"] = "openid",
            ["code_challenge"] = GeneratePkceS256Challenge(verifier),
            ["code_challenge_method"] = "S256",
        };
        var client = Factory.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Post, "/connect/par")
        {
            Content = new FormUrlEncodedContent(form),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($"{clientId}:{clientSecret}")));
        var resp = await client.SendAsync(req, TestContext.Current.CancellationToken);
        return resp.StatusCode;
    }

    private async Task CreateConfidentialClientAsync(
        string clientId, string clientSecret, string redirectUri, List<string> scopes, bool requirePar = false)
    {
        using var scope = Factory.Services.CreateScope();
        var oauthAdmin = scope.ServiceProvider.GetRequiredService<OAuthAdminService>();
        var dto = new CreateOAuthClientDto
        {
            ClientId = clientId,
            ClientSecret = clientSecret,
            ClientType = OAuthClientTypes.Confidential,
            ConsentType = OAuthConsentTypes.Implicit,
            DisplayName = clientId,
            RedirectUris = [redirectUri],
            PostLogoutRedirectUris = [],
            Scopes = scopes,
            AllowedGrantTypes = ["authorization_code", "refresh_token"],
            RequireConsent = false,
            AccessTokenType = AccessTokenType.Jwt,
            RequirePushedAuthorizationRequests = requirePar,
        };
        var result = await oauthAdmin.CreateClientAsync(dto, TestContext.Current.CancellationToken);
        if (result.IsError)
            throw new InvalidOperationException(
                $"CreateClientAsync failed: {string.Join(", ", result.Errors.Select(e => $"{e.Code}: {e.Description}"))}");
    }

    private static string GeneratePkceVerifier()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Base64UrlEncode(bytes);
    }

    private static string GeneratePkceS256Challenge(string verifier)
        => Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
