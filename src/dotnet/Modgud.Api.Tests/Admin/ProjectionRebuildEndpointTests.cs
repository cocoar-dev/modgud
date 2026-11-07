using System.Net;
using System.Text.Json;
using BuildingBlocks.Helper;
using Marten;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Application.DTOs.OAuth;
using Modgud.Application.Services;
using Modgud.Authorization.Apps;
using Modgud.Authorization.Events;
using Modgud.Authorization.Principals;
using Modgud.Domain.OAuth.Applications;
using Modgud.Domain.OAuth.Common;
using Modgud.Infrastructure.Persistence.Marten.Projections.Users;

namespace Modgud.Api.Tests.Admin;

/// <summary>
/// Exercises the admin projection-rebuild ENDPOINT (audit M8). The
/// ProjectionRebuildTests cover the rebuild mechanics by driving a daemon
/// directly; this one drives the HTTP endpoint so the per-tenant daemon pause
/// (<c>DaemonForDatabase(tenantId).StopAllAsync()</c>) and per-tenant side-effect
/// suppression are exercised end-to-end against the caller's realm.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class ProjectionRebuildEndpointTests : IntegrationTestBase
{
    public ProjectionRebuildEndpointTests(SharedPostgresFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Rebuild_Endpoint_Succeeds_And_Preserves_All_Principal_Subtypes()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await Factory.CreateTestUserWithIdentityAsync(
            "Reb", "Endpoint", "rbe", "rbe@test.com", "TestPass1234", isRealmAdmin: false);
        var authorizationGroup = await Factory.CreateTestGroupAsync(
            $"Endpoint_{Guid.NewGuid():N}"[..18], [user.Id]);
        var serviceAccount = new ServiceAccount
        {
            Id = Guid.CreateVersion7(),
            AccountName = $"endpoint-{Guid.NewGuid():N}"[..22],
            Purpose = "Projection endpoint regression",
            IsActive = true,
        };
        await using (var arrange = GetTenantedDocumentSession())
        {
            arrange.Store(serviceAccount);
            await arrange.SaveChangesAsync(ct);
        }

        var resp = await Client.PostAsync("/api/admin/projections/rebuild", null, ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        // Async views are rebuilt, both event-sourced Principal subtypes coexist,
        // and the directly stored ServiceAccount survives their shared table cleanup.
        await using var session = GetTenantedSession();
        var view = await session.LoadAsync<UserView>(user.Id, ct);
        Assert.NotNull(view);
        Assert.Equal("rbe@test.com", view!.Email);
        Assert.NotNull(await session.LoadAsync<Person>(user.Id, ct));
        Assert.NotNull(await session.LoadAsync<Group>(authorizationGroup.Id, ct));
        var preservedServiceAccount = await session.LoadAsync<ServiceAccount>(serviceAccount.Id, ct);
        Assert.NotNull(preservedServiceAccount);
        Assert.Equal(serviceAccount.Purpose, preservedServiceAccount!.Purpose);
    }

    /// <summary>B4-02 — the rebuild said "all projections" but replayed only the views, the
    /// principals and the roles. A drifted document of any other projection stayed drifted.</summary>
    [Fact]
    public async Task Rebuild_Endpoint_Replays_Every_Projection_And_Names_What_It_Rebuilt()
    {
        var ct = TestContext.Current.CancellationToken;
        // Written through their event streams, as the admin paths do.
        var appId = Guid.NewGuid();
        await using (var arrange = GetTenantedDocumentSession())
        {
            arrange.Events.StartStream<App>(appId, new AppCreatedEvent(
                Id: appId, Slug: $"rebuild-{appId:N}"[..20], DisplayName: "Rebuild probe", Description: null,
                Permissions: [], IsSystem: false));
            await arrange.SaveChangesAsync(ct);
        }
        Guid clientId;
        using (var scope = Factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>()
                .HttpContext = new DefaultHttpContext { Items = { ["TenantId"] = "system" } };
            var created = await scope.ServiceProvider.GetRequiredService<OAuthAdminService>().CreateClientAsync(new CreateOAuthClientDto
            {
                ClientId = $"rebuild-{appId:N}"[..20],
                ClientSecret = "rebuild-probe-secret",
                ClientType = OAuthClientTypes.Confidential,
                ConsentType = OAuthConsentTypes.Implicit,
                DisplayName = "Rebuild probe",
                RedirectUris = ["https://app.example/callback"],
                PostLogoutRedirectUris = [],
                Scopes = ["openid"],
                AllowedGrantTypes = ["authorization_code"],
                RequireConsent = false,
                AccessTokenType = AccessTokenType.Jwt,
            }, ct);
            Assert.False(created.IsError, created.IsError ? created.FirstError.Description : "");
            clientId = ShortGuid.TryParse(created.Value.Client.Id, out Guid parsed) ? parsed : Guid.Parse(created.Value.Client.Id);
        }

        // Drift: the projected documents are gone, their events are not.
        await using (var drift = GetTenantedDocumentSession())
        {
            drift.Delete<App>(appId);
            drift.Delete<OAuthApplicationState>(clientId);
            await drift.SaveChangesAsync(ct);
        }

        var resp = await Client.PostAsync("/api/admin/projections/rebuild", null, ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        await using var session = GetTenantedSession();
        Assert.NotNull(await session.LoadAsync<App>(appId, ct));
        Assert.NotNull(await session.LoadAsync<OAuthApplicationState>(clientId, ct));

        using var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var rebuilt = body.RootElement.GetProperty("Rebuilt").EnumerateArray().Select(e => e.GetString()).ToList();
        foreach (var name in new[]
                 {
                     "ViewProjections", "PersonProjection", "GroupProjection", "PositionPrincipalProjection",
                     "ServiceAccountProjection", "PermissionRoleProjection", "AppProjection",
                     "OAuthApplicationStateProjection", "OAuthScopeStateProjection", "OAuthApiStateProjection",
                     "LoginProviderProjection", "ExternalIdentityLinkProjection", "PositionGrantProjection",
                     "TerminalEnrollmentProjection", "StaffingSessionProjection", "AuthAuditViewProjection",
                 })
            Assert.Contains(name, rebuilt);
    }
}
