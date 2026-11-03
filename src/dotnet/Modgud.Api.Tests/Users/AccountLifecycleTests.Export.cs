using System.Net.Http.Json;
using Modgud.Authentication.Events;
using Modgud.Authentication.Gdpr;
using Modgud.Domain.OAuth.Applications;
using Modgud.Domain.OAuth.Storage;
using OpenIddict.Abstractions;

namespace Modgud.Api.Tests.Users;

/// <summary>
/// A.gdpr-C005 — the Article 20 export promised OAuth consents but carried none, read
/// login history only from the last 200 events of any kind (older logins vanished behind
/// other events) and swallowed read failures, all unmarked. It now exports the valid
/// consents, scans the whole stream for the most recent logins and states truncation,
/// read failures and deliberate exclusions in its metadata.
/// </summary>
public partial class AccountLifecycleTests
{
    [Fact]
    public async Task Export_contains_the_consents_and_marks_a_truncated_login_history()
    {
        var ct = TestContext.Current.CancellationToken;
        var (user, client) = await CreateUserWithClientAsync("EXPT");

        var appId = Guid.NewGuid();
        await using (var seed = GetTenantedDocumentSession())
        {
            seed.Store(new OAuthApplicationState { Id = appId, ClientId = "c005-app", DisplayName = "C005 App" });
            seed.Store(new OpenIddictAuthorizationDocument
            {
                ApplicationId = appId.ToString(),
                Subject = user.Id.ToString(),
                Status = OpenIddictConstants.Statuses.Valid,
                Type = OpenIddictConstants.AuthorizationTypes.Permanent,
                Scopes = ["openid", "profile"],
                CreationDate = DateTimeOffset.UtcNow.AddDays(-3),
            });
            // 120 logins, then many unrelated events after them.
            for (var i = 0; i < 120; i++)
                seed.Events.Append(user.Id, new UserLoggedInEvent(user.Id, "192.0.2.10", "password"));
            await seed.SaveChangesAsync(ct);
        }
        await using (var seed = GetTenantedDocumentSession())
        {
            for (var i = 0; i < 250; i++)
                seed.Events.Append(user.Id, new UserUnlockedEvent(user.Id));
            await seed.SaveChangesAsync(ct);
        }

        var export = await client.GetFromJsonAsync<UserDataExportDto>("/api/auth/export-data", JsonOptions, ct);

        var consent = Assert.Single(export!.OAuthConsents);
        Assert.Equal("c005-app", consent.ClientId);
        Assert.Equal(["openid", "profile"], consent.Scopes);
        Assert.Equal(100, export.LoginHistory.Count);
        Assert.True(export.Metadata.LoginHistoryTruncated);
        Assert.False(export.Metadata.LoginHistoryUnavailable);
        Assert.NotEmpty(export.Metadata.NotIncluded);
    }
}
