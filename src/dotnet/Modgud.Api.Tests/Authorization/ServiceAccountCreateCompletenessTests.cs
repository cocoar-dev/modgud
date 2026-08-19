using System.Net;
using System.Net.Http.Json;
using BuildingBlocks.Helper;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Application.DTOs.ServiceAccount;
using Modgud.Application.DTOs.OAuth;
using Modgud.Authorization.Events;
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
        var stream = await query.Events.FetchStreamAsync(serviceAccountId, token: ct);
        var createdEvent = Assert.IsType<ServiceAccountCreatedEvent>(Assert.Single(stream).Data);
        Assert.Equal(accountName, createdEvent.AccountName);
        Assert.False(createdEvent.IsActive);
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
        var stream = await query.Events.FetchStreamAsync(serviceAccountId, token: ct);
        Assert.IsType<ServiceAccountCreatedEvent>(Assert.Single(stream).Data);
    }

    [Fact]
    public async Task First_update_of_legacy_account_seeds_old_snapshot_then_records_update()
    {
        var ct = TestContext.Current.CancellationToken;
        var legacy = new ServiceAccount
        {
            Id = Guid.CreateVersion7(),
            AccountName = $"legacy-{Guid.NewGuid():N}"[..31],
            Purpose = "Before event sourcing",
            IsActive = true,
        };
        await using (var arrange = GetTenantedDocumentSession())
        {
            arrange.Store(legacy);
            await arrange.SaveChangesAsync(ct);
        }

        var response = await Client.PutAsJsonAsync(
            $"/api/service-account/{ShortGuid.Encode(legacy.Id)}",
            new { Purpose = "After event sourcing", IsActive = false },
            ct);
        response.EnsureSuccessStatusCode();

        await using var query = GetTenantedSession();
        var stream = await query.Events.FetchStreamAsync(legacy.Id, token: ct);
        Assert.Collection(
            stream,
            item =>
            {
                var created = Assert.IsType<ServiceAccountCreatedEvent>(item.Data);
                Assert.Equal("Before event sourcing", created.Purpose);
                Assert.True(created.IsActive);
            },
            item =>
            {
                var updated = Assert.IsType<ServiceAccountUpdatedEvent>(item.Data);
                Assert.Equal("After event sourcing", updated.Purpose);
                Assert.False(updated.IsActive);
            });

        var persisted = await query.LoadAsync<ServiceAccount>(legacy.Id, ct);
        Assert.Equal("After event sourcing", persisted!.Purpose);
        Assert.False(persisted.IsActive);
    }
}
