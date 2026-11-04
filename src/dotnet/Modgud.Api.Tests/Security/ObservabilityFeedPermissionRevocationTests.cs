using System.Net;
using System.Security.Claims;
using BuildingBlocks.Helper;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Realtime;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Infrastructure.Persistence.Tenancy;

namespace Modgud.Api.Tests.Security;

/// <summary>
/// gvm6 — the observability feeds (activity and error stream of <c>ObservabilityHub</c>)
/// checked <c>observability:read</c> once, at subscribe time, and then pushed every later
/// item until the client disconnected. Unlike the entity streams (B9-RT01) these are
/// high-rate feeds fed from synchronous handlers, so the gate re-checks on a timer and
/// detaches and completes once the permission is gone.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class ObservabilityFeedPermissionRevocationTests(SharedPostgresFixture fixture) : IntegrationTestBase(fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_feed_stops_and_detaches_once_the_read_permission_is_withdrawn()
    {
        var reader = await Factory.CreateTestUserWithIdentityAsync("Feed", "Reader", "fr", "fr@test.com");
        var role = await Factory.CreateTestRoleAsync("Feed readers", [("observability", "read")]);
        var group = await Factory.CreateTestGroupAsync("Feed readers", [reader.Id], [role.Id]);

        using var scope = Factory.Services.CreateScope();
        var http = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, reader.Id.ToString())], "Test")),
        };
        http.Items[TenantConstants.HttpContextTenantIdKey] = TenantConstants.SystemTenantId;

        Action<string>? push = null;
        var detached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new List<string>();
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var subscription = HubAuthorization
            .AuthorizedRealmFeed<string>(http, TenantConstants.SystemTenantId, "observability:read",
                TimeSpan.FromMilliseconds(300),
                onNext => { push = onNext; return () => detached.TrySetResult(); })
            .Subscribe(
                item => { lock (received) received.Add(item); first.TrySetResult(); },
                _ => completed.TrySetResult(),
                () => completed.TrySetResult());

        for (var i = 0; i < 300 && push is null; i++) await Task.Delay(100, Ct);
        Assert.NotNull(push);
        push!("before");
        await first.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        // The permission is withdrawn; the browser session (and the connection) stays.
        using var deleted = await Client.DeleteAsync($"/api/group/{new ShortGuid(group.Id)}", Ct);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        // Within a few re-check intervals the feed ends and lets go of the buffer handler.
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        await detached.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        push("after");
        lock (received) Assert.Equal(["before"], received);
    }
}
