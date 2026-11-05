using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Modgud.Api.Tests.Infrastructure;

namespace Modgud.Api.Tests.ColdStart;

/// <summary>
/// FD-03 — secret sanitization skipped an entity whose key is empty before removing its
/// secret, so a client with an empty ClientId kept its ClientSecret in plain text in the stored
/// draft and in every draft response. FD-04 — an explicit <c>CaptchaSecret: null</c> (a staged
/// clear) was overwritten by a captcha secret staged earlier in the same draft.
/// </summary>
public class RealmDraftSecretSanitizationTests(ColdStartFixture fixture) : ColdStartTestBase(fixture)
{
    [Fact]
    public async Task A_secret_on_an_entity_without_a_key_is_never_stored()
    {
        await using var host = await Fixture.CreateIsolatedHostAsync();
        var factory = host.Factory;
        var ct = TestContext.Current.CancellationToken;
        var client = await factory.CreateRealmAdminAndLoginAsync();

        var create = JsonNode.Parse("""
            { "Name": "fd03", "Source": "manifest",
              "Manifest": { "Realm": {}, "Clients": [ { "ClientId": "", "ClientType": "confidential", "ClientSecret": "plain-fd03-secret" } ] } }
            """)!;
        var created = await client.PostAsJsonAsync("/api/admin/realm-config/drafts", create, factory.JsonOptions, ct);
        var body = await created.Content.ReadAsStringAsync(ct);
        Assert.True(created.StatusCode == HttpStatusCode.OK, $"draft create {(int)created.StatusCode}: {body}");
        Assert.DoesNotContain("plain-fd03-secret", body);

        var id = JsonNode.Parse(body)!["Id"]!.GetValue<Guid>();
        var reread = await client.GetStringAsync($"/api/admin/realm-config/drafts/{id}", ct);
        Assert.DoesNotContain("plain-fd03-secret", reread);
    }

    [Fact]
    public async Task An_explicit_captcha_null_clears_the_staged_captcha_secret()
    {
        await using var host = await Fixture.CreateIsolatedHostAsync();
        var factory = host.Factory;
        var ct = TestContext.Current.CancellationToken;
        var client = await factory.CreateRealmAdminAndLoginAsync();
        const string slot = "settings/SelfRegistration/CaptchaSecret";

        var create = JsonNode.Parse("""
            { "Name": "fd04", "Source": "manifest",
              "Manifest": { "Realm": {}, "Settings": { "SelfRegistration": { "CaptchaSecret": "captcha-fd04" } } } }
            """)!;
        var created = await client.PostAsJsonAsync("/api/admin/realm-config/drafts", create, factory.JsonOptions, ct);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var draft = JsonNode.Parse(await created.Content.ReadAsStringAsync(ct))!;
        Assert.Contains(slot, draft["SecretSlots"]!.AsArray().Select(s => s!.GetValue<string>()));

        var clearing = JsonNode.Parse("""
            { "Realm": {}, "Settings": { "SelfRegistration": { "CaptchaSecret": null } } }
            """)!;
        var updated = await client.PutAsJsonAsync($"/api/admin/realm-config/drafts/{draft["Id"]!.GetValue<Guid>()}",
            new { ExpectedVersion = draft["Version"]!.GetValue<int>(), Manifest = clearing }, factory.JsonOptions, ct);
        var updatedBody = await updated.Content.ReadAsStringAsync(ct);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var after = JsonNode.Parse(updatedBody)!;
        Assert.DoesNotContain(slot, after["SecretSlots"]!.AsArray().Select(s => s!.GetValue<string>()));
    }
}
