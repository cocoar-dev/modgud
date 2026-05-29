using System.Net.Http.Json;
using BuildingBlocks.Helper;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Authentication.Domain;
using Modgud.Authentication.Domain.ExternalAuth;
using Modgud.Authentication.Domain.ExternalAuth.Events;
using Modgud.Authentication.Gdpr;
using Modgud.Domain.OAuth.Storage;

namespace Modgud.Api.Tests.Security;

/// <summary>
/// Hotfix C — a user's live access (OpenIddict tokens + authorizations + device
/// sessions) must be revoked when the user is deleted or deactivated, and a GDPR
/// erase must scrub the user's external identity links (Email + raw IdP claims).
/// Before this fix none of the lifecycle paths touched OpenIddict grants/sessions
/// and GDPR left ExternalIdentityLink rows fully intact.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class UserLifecycleRevocationTests : IntegrationTestBase
{
    public UserLifecycleRevocationTests(SharedPostgresFixture fixture) : base(fixture) { }

    // OpenIddictConstants.Statuses values, as written by the Marten stores.
    private const string Valid = "valid";
    private const string Revoked = "revoked";

    private async Task SeedGrantsAndSessionAsync(Guid userId)
    {
        var subject = userId.ToString();
        await using var seed = GetTenantedDocumentSession();
        seed.Store(new OpenIddictTokenDocument { Subject = subject, Status = Valid, Type = "access_token" });
        seed.Store(new OpenIddictTokenDocument { Subject = subject, Status = Valid, Type = "refresh_token" });
        seed.Store(new OpenIddictAuthorizationDocument { Subject = subject, Status = Valid, Type = "permanent" });
        seed.Store(new UserSession
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
            LastActiveAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
        });
        await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Delete_revokes_tokens_authorizations_and_sessions()
    {
        var user = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Del", lastname: "Revoke", acronym: "DR", email: "del-revoke@test.com");
        await SeedGrantsAndSessionAsync(user.Id);

        var response = await Client.DeleteAsync(
            $"/api/user/{new ShortGuid(user.Id)}", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        var subject = user.Id.ToString();
        await using var read = GetTenantedSession();

        var tokens = await read.Query<OpenIddictTokenDocument>()
            .Where(t => t.Subject == subject).ToListAsync(TestContext.Current.CancellationToken);
        Assert.NotEmpty(tokens);
        Assert.All(tokens, t => Assert.Equal(Revoked, t.Status));

        var authorizations = await read.Query<OpenIddictAuthorizationDocument>()
            .Where(a => a.Subject == subject).ToListAsync(TestContext.Current.CancellationToken);
        Assert.NotEmpty(authorizations);
        Assert.All(authorizations, a => Assert.Equal(Revoked, a.Status));

        var sessions = await read.Query<UserSession>()
            .Where(s => s.UserId == user.Id).ToListAsync(TestContext.Current.CancellationToken);
        Assert.Empty(sessions);
    }

    [Fact]
    public async Task Deactivate_revokes_tokens_and_sessions_but_keeps_consent()
    {
        var user = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Deact", lastname: "Revoke", acronym: "DV", email: "deact-revoke@test.com");
        await SeedGrantsAndSessionAsync(user.Id);

        var response = await Client.PutAsJsonAsync(
            $"/api/user/{new ShortGuid(user.Id)}/active",
            new { IsActive = false },
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        var subject = user.Id.ToString();
        await using var read = GetTenantedSession();

        // Tokens die immediately (reference tokens).
        var tokens = await read.Query<OpenIddictTokenDocument>()
            .Where(t => t.Subject == subject).ToListAsync(TestContext.Current.CancellationToken);
        Assert.NotEmpty(tokens);
        Assert.All(tokens, t => Assert.Equal(Revoked, t.Status));

        // Consent grants are KEPT on a reversible deactivation — reactivation
        // must not drag the user back through the consent screen.
        var authorizations = await read.Query<OpenIddictAuthorizationDocument>()
            .Where(a => a.Subject == subject).ToListAsync(TestContext.Current.CancellationToken);
        Assert.NotEmpty(authorizations);
        Assert.All(authorizations, a => Assert.Equal(Valid, a.Status));

        var sessions = await read.Query<UserSession>()
            .Where(s => s.UserId == user.Id).ToListAsync(TestContext.Current.CancellationToken);
        Assert.Empty(sessions);
    }

    [Fact]
    public async Task GdprErase_scrubs_external_identity_links()
    {
        var user = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Gdpr", lastname: "Erase", acronym: "GE", email: "gdpr-erase@test.com");

        var activeLinkId = Guid.NewGuid();
        var unlinkedLinkId = Guid.NewGuid();
        var providerId = Guid.NewGuid();

        using (var scope = Factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            // An active link…
            session.Events.StartStream<ExternalIdentityLink>(activeLinkId,
                new ExternalIdentityLinkedEvent(
                    Id: activeLinkId, UserId: user.Id, LoginProviderId: providerId,
                    Issuer: "https://idp.test", Subject: "sub-active",
                    Email: "gdpr-erase@test.com", DisplayName: "Gdpr Erase",
                    LinkedAt: DateTimeOffset.UtcNow));
            // …and an already-unlinked tombstone, which still carries PII.
            session.Events.StartStream<ExternalIdentityLink>(unlinkedLinkId,
                new ExternalIdentityLinkedEvent(
                    Id: unlinkedLinkId, UserId: user.Id, LoginProviderId: providerId,
                    Issuer: "https://idp.test", Subject: "sub-old",
                    Email: "gdpr-erase@test.com", DisplayName: "Gdpr Erase",
                    LinkedAt: DateTimeOffset.UtcNow));
            session.Events.Append(unlinkedLinkId,
                new ExternalIdentityUnlinkedEvent(unlinkedLinkId, DateTimeOffset.UtcNow, null));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using (var scope = Factory.Services.CreateScope())
        {
            var gdpr = scope.ServiceProvider.GetRequiredService<IGdprService>();
            var result = await gdpr.PermanentlyEraseAsync(
                user.Id, adminUserId: null, reason: "test-erase", TestContext.Current.CancellationToken);
            Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        }

        await using var read = GetTenantedSession();
        Assert.Null(await read.LoadAsync<ExternalIdentityLink>(activeLinkId, TestContext.Current.CancellationToken));
        Assert.Null(await read.LoadAsync<ExternalIdentityLink>(unlinkedLinkId, TestContext.Current.CancellationToken));
    }
}
