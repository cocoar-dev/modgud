using Marten;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Authentication.Domain.Saml;
using Modgud.Authentication.Identity.LoginProviders.Saml;

namespace Modgud.Api.Tests.Security;

/// <summary>
/// Pins SAML request correlation and single-use (P0-2).
///
/// <para>
/// The ACS validated status and signatures but never checked that the Response
/// answered a request this SP actually sent: <c>StartLoginAsync</c> discarded the
/// AuthnRequest ID and <c>InResponseTo</c> appeared nowhere in the SAML code. A
/// captured, genuinely-signed Response could therefore be presented repeatedly
/// within its <c>NotOnOrAfter</c> window — replay, login-CSRF, session swapping.
/// </para>
///
/// <para>
/// These tests drive <see cref="MartenSamlAuthnRequestStore"/> against real
/// Postgres, which is where the guarantee lives. The full HTTP ACS path is not
/// exercised end to end because the repo has no rig for minting signed SAML
/// Responses; the flow's wiring of this store is covered by review, not by test.
/// </para>
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class SamlReplayProtectionTests : IntegrationTestBase
{
    public SamlReplayProtectionTests(SharedPostgresFixture fixture) : base(fixture) { }

    private static string NewRequestId() => $"_id{Guid.NewGuid():N}";

    [Fact]
    public async Task A_response_can_answer_its_request_exactly_once()
    {
        var ct = TestContext.Current.CancellationToken;
        var requestId = NewRequestId();
        var providerId = Guid.NewGuid();

        await using (var s = GetTenantedDocumentSession())
            await new MartenSamlAuthnRequestStore(s).RecordAsync(requestId, providerId, ct);

        await using (var s = GetTenantedDocumentSession())
        {
            var first = await new MartenSamlAuthnRequestStore(s)
                .TryConsumeAsync(requestId, providerId, ct);
            Assert.Equal(SamlAuthnRequestConsumeResult.Consumed, first);
        }

        // The replay: the very same captured Response presented again.
        await using (var s = GetTenantedDocumentSession())
        {
            var second = await new MartenSamlAuthnRequestStore(s)
                .TryConsumeAsync(requestId, providerId, ct);
            Assert.Equal(SamlAuthnRequestConsumeResult.AlreadyConsumed, second);
        }
    }

    [Fact]
    public async Task Concurrent_presentations_of_one_response_cannot_both_win()
    {
        var ct = TestContext.Current.CancellationToken;
        var requestId = NewRequestId();
        var providerId = Guid.NewGuid();

        await using (var s = GetTenantedDocumentSession())
            await new MartenSamlAuthnRequestStore(s).RecordAsync(requestId, providerId, ct);

        // Separate sessions, both loading before either commits — the shape a
        // load-then-delete consume would lose, since Marten does not version-check
        // deletes.
        await using var sA = GetTenantedDocumentSession();
        await using var sB = GetTenantedDocumentSession();
        var storeA = new MartenSamlAuthnRequestStore(sA);
        var storeB = new MartenSamlAuthnRequestStore(sB);

        var results = await Task.WhenAll(
            storeA.TryConsumeAsync(requestId, providerId, ct),
            storeB.TryConsumeAsync(requestId, providerId, ct));

        Assert.Equal(1, results.Count(r => r == SamlAuthnRequestConsumeResult.Consumed));
        Assert.Equal(1, results.Count(r => r == SamlAuthnRequestConsumeResult.AlreadyConsumed));
    }

    [Fact]
    public async Task An_unsolicited_response_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var s = GetTenantedDocumentSession();
        var store = new MartenSamlAuthnRequestStore(s);

        // IdP-initiated SSO: no InResponseTo at all. Only SP-initiated login
        // exists in this codebase, so an unsolicited Response is never legitimate.
        Assert.Equal(SamlAuthnRequestConsumeResult.Unsolicited,
            await store.TryConsumeAsync(null, Guid.NewGuid(), ct));
        Assert.Equal(SamlAuthnRequestConsumeResult.Unsolicited,
            await store.TryConsumeAsync("   ", Guid.NewGuid(), ct));
    }

    [Fact]
    public async Task A_response_referencing_an_unknown_request_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var s = GetTenantedDocumentSession();

        Assert.Equal(SamlAuthnRequestConsumeResult.Unknown,
            await new MartenSamlAuthnRequestStore(s)
                .TryConsumeAsync(NewRequestId(), Guid.NewGuid(), ct));
    }

    [Fact]
    public async Task A_response_cannot_be_presented_at_a_different_providers_acs()
    {
        var ct = TestContext.Current.CancellationToken;
        var requestId = NewRequestId();
        var solicitedFrom = Guid.NewGuid();
        var presentedAt = Guid.NewGuid();

        await using (var s = GetTenantedDocumentSession())
            await new MartenSamlAuthnRequestStore(s).RecordAsync(requestId, solicitedFrom, ct);

        await using (var s = GetTenantedDocumentSession())
        {
            Assert.Equal(SamlAuthnRequestConsumeResult.ProviderMismatch,
                await new MartenSamlAuthnRequestStore(s)
                    .TryConsumeAsync(requestId, presentedAt, ct));
        }

        // And the mismatch must not have spent it for the rightful provider.
        await using (var s = GetTenantedDocumentSession())
        {
            Assert.Equal(SamlAuthnRequestConsumeResult.Consumed,
                await new MartenSamlAuthnRequestStore(s)
                    .TryConsumeAsync(requestId, solicitedFrom, ct));
        }
    }

    [Fact]
    public async Task A_stale_request_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        var requestId = NewRequestId();
        var providerId = Guid.NewGuid();

        await using (var seed = GetTenantedDocumentSession())
        {
            seed.Insert(new SamlPendingAuthnRequest
            {
                Id = requestId,
                LoginProviderId = providerId,
                CreatedAt = DateTimeOffset.UtcNow.AddHours(-2),
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(-1),
            });
            await seed.SaveChangesAsync(ct);
        }

        await using var s = GetTenantedDocumentSession();
        Assert.Equal(SamlAuthnRequestConsumeResult.Expired,
            await new MartenSamlAuthnRequestStore(s).TryConsumeAsync(requestId, providerId, ct));
    }

    [Fact]
    public async Task Recording_a_request_prunes_ones_that_can_no_longer_be_answered()
    {
        var ct = TestContext.Current.CancellationToken;
        var stale = NewRequestId();
        var providerId = Guid.NewGuid();

        await using (var seed = GetTenantedDocumentSession())
        {
            seed.Insert(new SamlPendingAuthnRequest
            {
                Id = stale,
                LoginProviderId = providerId,
                CreatedAt = DateTimeOffset.UtcNow.AddHours(-2),
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(-1),
            });
            await seed.SaveChangesAsync(ct);
        }

        await using (var s = GetTenantedDocumentSession())
            await new MartenSamlAuthnRequestStore(s).RecordAsync(NewRequestId(), providerId, ct);

        await using var check = GetTenantedDocumentSession();
        Assert.Null(await check.LoadAsync<SamlPendingAuthnRequest>(stale, ct));
    }
}
