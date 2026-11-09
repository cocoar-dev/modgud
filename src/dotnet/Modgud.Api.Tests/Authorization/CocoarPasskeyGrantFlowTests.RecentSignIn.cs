using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Application.DTOs.RealmSettings;
using Modgud.Authentication.RealmSettings;

namespace Modgud.Api.Tests.Authorization;

/// <summary>
/// RV-0001 stage 2 — with <c>RequireRecentSignInForPasskeyChanges</c> on, a native passkey
/// change needs a sign-in from the last 15 minutes. A token from a sign-in just made passes,
/// whatever the factor: a user whose passkey device is gone signs in with an e-mail code or
/// magic link on the new device, then removes the old passkey and enrolls a new one. (The
/// refusal of a stale token is pinned by <c>NativeRecentSignInTests</c>: a token cannot be
/// aged here without moving the server's clock.)
/// </summary>
public partial class CocoarPasskeyGrantFlowTests
{
    [Fact]
    public async Task Switched_on_a_fresh_sign_in_may_enroll_and_remove_passkeys()
    {
        await EnableNativeGrantsAsync();
        await RequireRecentSignInAsync(true);

        var appId = await SeedNativeAppAsync([ListedAndroidOrigin]);
        await SeedPasskeyClientAsync("recent-enroll", rpId: AndroidRpId, appId: appId);
        var bearer = await BearerForAsync("recent-enroll");
        var (status, body, _) = await EnrollAsync(bearer, ListedAndroidOrigin);
        Assert.True(status is >= 200 and < 300, $"enroll with a fresh sign-in failed ({status}): {body}");

        var token = await MintAccessTokenAsync();
        var lostPhone = await SeedOwnedCredentialAsync(DefaultUser!.Id, "Lost Phone");
        var removed = await BearerClient(token).DeleteAsync($"/connect/passkey/{lostPhone}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
    }

    [Fact]
    public async Task The_switch_is_off_by_default_and_round_trips_through_the_realm_settings()
    {
        using (var scope = NewSystemTenantScope())
        {
            var settings = scope.ServiceProvider.GetRequiredService<IRealmSettingsService>();
            var initial = await settings.GetDtoAsync(TestContext.Current.CancellationToken);
            Assert.False(initial.NativeGrants.RequireRecentSignInForPasskeyChanges);
        }

        await RequireRecentSignInAsync(true);

        using var json = JsonDocument.Parse(await Client.GetStringAsync("/api/admin/realm-settings", TestContext.Current.CancellationToken));
        Assert.True(json.RootElement.GetProperty("NativeGrants").GetProperty("RequireRecentSignInForPasskeyChanges").GetBoolean());
    }

    private async Task RequireRecentSignInAsync(bool on)
    {
        using var scope = NewSystemTenantScope();
        var settings = scope.ServiceProvider.GetRequiredService<IRealmSettingsService>();
        var result = await settings.PatchAsync(new UpdateRealmSettingsDto
        {
            NativeGrants = new UpdateNativeGrantSettingsDto { RequireRecentSignInForPasskeyChanges = on },
        }, TestContext.Current.CancellationToken);
        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : "");
    }
}
