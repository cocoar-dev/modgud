using System.Net;
using System.Reactive.Subjects;
using System.Security.Claims;
using BuildingBlocks.Helper;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Realtime;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Infrastructure.Persistence.Tenancy;

namespace Modgud.Api.Tests.Security;

/// <summary>
/// B9-RT01 — the privileged entity streams (users, service accounts, positions, invite
/// codes, apps, OAuth clients and scopes) checked the read permission once, at subscribe
/// time. A caller whose permission was withdrawn while the browser session stayed alive
/// kept receiving every later payload. The gate now re-checks before each item and ends
/// the stream once the permission is gone.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class HubStreamPermissionRevocationTests(SharedPostgresFixture fixture) : IntegrationTestBase(fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_stream_stops_delivering_once_the_read_permission_is_withdrawn()
    {
        var reader = await Factory.CreateTestUserWithIdentityAsync("Stream", "Reader", "sr", "sr@test.com");
        var role = await Factory.CreateTestRoleAsync("Stream readers", [("user", "read")]);
        var group = await Factory.CreateTestGroupAsync("Stream readers", [reader.Id], [role.Id]);

        using var scope = Factory.Services.CreateScope();
        var http = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, reader.Id.ToString())], "Test")),
        };
        http.Items[TenantConstants.HttpContextTenantIdKey] = TenantConstants.SystemTenantId;

        using var source = new Subject<string>();
        var received = new List<string>();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = HubAuthorization
            .AuthorizedRealmStream(http, TenantConstants.SystemTenantId, "user:read", source)
            .Subscribe(
                item => { lock (received) received.Add(item); first.TrySetResult(); },
                _ => completed.TrySetResult(),
                () => completed.TrySetResult());

        // Subscribed while authorized: the first payload arrives.
        await WaitForSubscriptionAsync(source);
        source.OnNext("before");
        await first.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        // The permission is withdrawn; the browser session (and the connection) stays.
        using var deleted = await Client.DeleteAsync($"/api/group/{new ShortGuid(group.Id)}", Ct);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        source.OnNext("after");
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        lock (received) Assert.Equal(["before"], received);
    }

    private static async Task WaitForSubscriptionAsync(Subject<string> source)
    {
        // The gate subscribes to the source only after its async permission check.
        for (var i = 0; i < 300 && !source.HasObservers; i++) await Task.Delay(100, Ct);
        Assert.True(source.HasObservers);
    }
}
