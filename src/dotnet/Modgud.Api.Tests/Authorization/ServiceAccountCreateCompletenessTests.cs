using System.Net;
using System.Net.Http.Json;
using BuildingBlocks.Helper;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Application.DTOs.ServiceAccount;
using Modgud.Application.DTOs.OAuth;
using Modgud.Authorization.Principals;
using Modgud.Domain.OAuth.Applications;

namespace Modgud.Api.Tests.Authorization;

public class ServiceAccountCreateCompletenessTests(SharedPostgresFixture fixture)
    : IntegrationTestBase(fixture)
{
    [Fact]
    public async Task Create_can_commit_status_and_initial_credential_together()
    {
        var ct = TestContext.Current.CancellationToken;
        var accountName = $"complete-{Guid.NewGuid():N}"[..32];

        var response = await Client.PostAsJsonAsync("/api/service-account", new
        {
            AccountName = accountName,
            Purpose = "Atomic create test",
            IsActive = false,
            InitialCredential = new
            {
                DisplayName = "Initial deployment credential",
                Scopes = Array.Empty<string>(),
                AppIds = Array.Empty<string>(),
                AccessTokenLifetime = 900,
                AccessTokenType = "Reference",
                Enabled = false,
            },
        }, ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<ServiceAccountDto>(JsonOptions, ct);
        Assert.NotNull(created);
        Assert.False(created.IsActive);
        Assert.NotNull(created.InitialCredential);
        Assert.False(string.IsNullOrWhiteSpace(created.InitialCredential.ClientSecret));
        Assert.False(created.InitialCredential.Credential.Enabled);
        Assert.True(ShortGuid.TryParse(created.Id, out Guid serviceAccountId));

        using var scope = Factory.Services.CreateScope();
        var query = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var serviceAccount = await query.LoadAsync<ServiceAccount>(serviceAccountId, ct);
        var credential = await query.Query<OAuthApplicationState>()
            .SingleAsync(x => x.LinkedServiceAccountId == serviceAccountId && !x.IsDeleted, ct);

        Assert.False(serviceAccount!.IsActive);
        Assert.Contains(OAuthApplicationPropertyKeys.Enabled, credential.Properties.Keys);
        Assert.Equal("900", credential.Settings[OAuthApplicationSettingKeys.AccessTokenLifetime]);
    }

    [Fact]
    public async Task Invalid_initial_credential_leaves_no_service_account()
    {
        var ct = TestContext.Current.CancellationToken;
        var accountName = $"invalid-{Guid.NewGuid():N}"[..31];

        var response = await Client.PostAsJsonAsync("/api/service-account", new
        {
            AccountName = accountName,
            InitialCredential = new
            {
                Scopes = Array.Empty<string>(),
                AppIds = Array.Empty<string>(),
                AccessTokenLifetime = 30,
                AccessTokenType = "Reference",
            },
        }, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var scope = Factory.Services.CreateScope();
        var query = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var serviceAccount = await query.Query<ServiceAccount>()
            .FirstOrDefaultAsync(x => x.AccountName == accountName, ct);
        Assert.Null(serviceAccount);
    }

    [Fact]
    public async Task OAuth_client_inline_service_account_preserves_create_status()
    {
        var ct = TestContext.Current.CancellationToken;
        var accountName = $"oauth-inline-{Guid.NewGuid():N}"[..31];

        var response = await Client.PostAsJsonAsync("/api/admin/oauth/clients", new
        {
            ClientId = $"client-{Guid.NewGuid():N}",
            ClientType = "confidential",
            ConsentType = "implicit",
            AllowedGrantTypes = new[] { "client_credentials" },
            Scopes = Array.Empty<string>(),
            AppIds = Array.Empty<string>(),
            RequireClientSecret = true,
            NewServiceAccount = new
            {
                AccountName = accountName,
                Purpose = "Created from OAuth client",
                IsActive = false,
            },
        }, ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<OAuthClientCreatedDto>(JsonOptions, ct);
        Assert.NotNull(created?.CreatedServiceAccount);
        Assert.False(created.CreatedServiceAccount.IsActive);

        Assert.True(ShortGuid.TryParse(created.CreatedServiceAccount.Id, out Guid serviceAccountId));
        using var scope = Factory.Services.CreateScope();
        var query = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var serviceAccount = await query.LoadAsync<ServiceAccount>(serviceAccountId, ct);
        Assert.False(serviceAccount!.IsActive);
    }
}
