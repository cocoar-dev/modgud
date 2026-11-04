using System.Net.Http.Json;
using System.Text.Json;
using BuildingBlocks.Helper;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Authentication.Api.ExternalAuth;
using Modgud.Authentication.Domain.ExternalAuth;
using Modgud.Authentication.Domain.ExternalAuth.Events;
using Modgud.Authentication.Domain.LoginProviders;
using Modgud.Infrastructure.Persistence.Tenancy;

namespace Modgud.Api.Tests.ExternalAuth;

/// <summary>
/// EP-03 — a login provider offers <c>RawClaimsRetentionDays</c>, but nothing consumed it:
/// the last raw-claims snapshot was delivered (admin user view, script editor sample) and
/// kept — on the link and in every login event of its stream — however old it was. Expired
/// snapshots are now withheld at once and removed by the realm's retention sweep; the rest
/// of the login events (email, display name, script output) stays.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public partial class RawClaimsRetentionTests : IntegrationTestBase
{
    public RawClaimsRetentionTests(SharedPostgresFixture fixture) : base(fixture) { }

    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public async Task Expired_raw_claims_are_not_delivered()
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = await CreateProviderAsync(retentionDays: 1);
        var userId = Guid.NewGuid();
        await SeedLinkAsync(userId, provider.Id, Now.AddDays(-3));

        var links = await Client.GetFromJsonAsync<JsonElement>($"/api/admin/users/{new ShortGuid(userId)}/external-links", ct);
        Assert.Equal(JsonValueKind.Null, RawClaimsKind(Assert.Single(links.EnumerateArray())));

        var sample = await Client.GetFromJsonAsync<JsonElement>($"/api/admin/login-providers/{new ShortGuid(provider.Id)}/last-raw-claims", ct);
        Assert.False(sample.GetProperty("Available").GetBoolean());
    }

    [Fact]
    public async Task Raw_claims_within_the_retention_are_delivered()
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = await CreateProviderAsync(retentionDays: 1);
        var userId = Guid.NewGuid();
        await SeedLinkAsync(userId, provider.Id, Now.AddHours(-1));

        var links = await Client.GetFromJsonAsync<JsonElement>($"/api/admin/users/{new ShortGuid(userId)}/external-links", ct);
        Assert.Equal(JsonValueKind.Object, RawClaimsKind(Assert.Single(links.EnumerateArray())));

        var sample = await Client.GetFromJsonAsync<JsonElement>($"/api/admin/login-providers/{new ShortGuid(provider.Id)}/last-raw-claims", ct);
        Assert.True(sample.GetProperty("Available").GetBoolean());
    }

    /// <summary>The API leaves null fields out; a missing snapshot counts as null.</summary>
    private static JsonValueKind RawClaimsKind(JsonElement link)
        => link.TryGetProperty("LastRawClaims", out var raw) ? raw.ValueKind : JsonValueKind.Null;

    /// <summary>A cleared snapshot is stored as JSON null and reads back as a Null-rooted document.</summary>
    private static bool HasRawClaims(ExternalIdentityLink? link)
        => link?.LastRawClaims is { } doc && doc.RootElement.ValueKind != JsonValueKind.Null;

    private async Task<LoginProvider> CreateProviderAsync(int? retentionDays)
    {
        var provider = new LoginProvider
        {
            Id = Guid.NewGuid(),
            Type = LoginProviderType.Oidc,
            Flavor = LoginProviderFlavor.GenericOidc,
            Slug = $"r{Guid.NewGuid():N}"[..12],
            DisplayName = $"Retention-{Guid.NewGuid():N}"[..18],
            Enabled = false,
            StoreRawClaims = true,
            RawClaimsRetentionDays = retentionDays,
            CreatedAt = Now,
            UpdatedAt = Now,
        };
        await using var session = GetTenantedDocumentSession();
        session.Store(provider);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return provider;
    }

    /// <summary>A link with one login event per capture time, each carrying raw claims.</summary>
    private async Task<Guid> SeedLinkAsync(Guid userId, Guid providerId, params DateTimeOffset[] captures)
    {
        var linkId = Guid.NewGuid();
        await using var session = GetTenantedDocumentSession();
        session.Events.StartStream<ExternalIdentityLink>(linkId, new ExternalIdentityLinkedEvent(
            Id: linkId, UserId: userId, LoginProviderId: providerId,
            Issuer: "https://idp.retention.test", Subject: $"sub-{linkId:N}",
            Email: "person@idp.test", DisplayName: "Person", LinkedAt: captures[0]));
        foreach (var capturedAt in captures)
        {
            session.Events.Append(linkId, new ExternalIdentityScriptRecordedEvent(
                Id: linkId, CapturedAt: capturedAt, ScriptSucceeded: true,
                ScriptOutput: JsonDocument.Parse("""{"email":"person@idp.test"}"""),
                ScriptError: null,
                RawClaims: JsonDocument.Parse("""{"sub":"x","email":"person@idp.test","groups":["a"]}"""),
                Email: "person@idp.test", DisplayName: "Person"));
        }
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return linkId;
    }

    private static async Task<List<JsonElement>> LoginEventsAsync(IQuerySession query, Guid linkId)
    {
        var schema = query.DocumentStore.Options.Events.DatabaseSchemaName;
        var rows = await query.QueryAsync<string>(
            $"select data::text from \"{schema}\".mt_events where stream_id = ? and type = 'external_identity_script_recorded' order by version",
            TestContext.Current.CancellationToken, linkId);
        return rows.Select(r => JsonDocument.Parse(r).RootElement).ToList();
    }
}