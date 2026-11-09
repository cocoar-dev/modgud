using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Kit = Modgud.Provisioning.TestKit;

namespace Modgud.Api.Tests.Provisioning;

/// <summary>
/// Client-side contracts of the provisioning TestKit, against a recording HTTP handler — no
/// server, no database. B10-09: an explicit reference id is the author's identity and must
/// reach the wire unchanged. B10-10: cleanup must still be sent when the caller's token is
/// already cancelled, and disposing must not throw over the test's own outcome.
/// </summary>
public sealed class TestKitClientContractTests
{
    private static readonly Kit.RealmSpec Realm = new() { Slug = "kitcontract", DisplayName = "Kit", Domains = ["kit.localhost"] };

    [Fact]
    public async Task An_explicit_reference_id_is_not_replaced_by_a_declared_alias()
    {
        var pinned = Guid.NewGuid().ToString();
        var handler = new RecordingHandler();
        var kit = new Kit.ModgudProvisioningClient(handler.Client);

        await using var realm = await kit.ImportRealmAsync(Realm, new Kit.RealmManifest
        {
            Users = [new Kit.RealmManifestUser { Key = "alice", Email = "alice@kit.localhost" }],
            Groups = [new Kit.RealmManifestGroup { Name = "Ops", Members = [new Kit.ManifestRef { Key = "alice", Id = pinned }] }],
        }, TestContext.Current.CancellationToken);

        var applied = JsonNode.Parse(handler.Bodies.Single(b => b.Path.EndsWith("/apply", StringComparison.Ordinal)).Body)!;
        var member = applied["Groups"]![0]!["Members"]![0]!;
        Assert.Equal(pinned, member is JsonValue ? member.GetValue<string>() : member["Id"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_failed_apply_still_sends_the_cleanup_when_the_token_is_cancelled()
    {
        using var cts = new CancellationTokenSource();
        var handler = new RecordingHandler { FailApply = true, OnApply = cts.Cancel };
        var kit = new Kit.ModgudProvisioningClient(handler.Client);

        await Assert.ThrowsAnyAsync<Exception>(() => kit.ImportRealmAsync(Realm, new Kit.RealmManifest(), cts.Token));

        Assert.Contains(handler.Bodies, b => b.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task Dispose_does_not_throw_when_the_cleanup_transport_fails()
    {
        var handler = new RecordingHandler { FailDeleteTransport = true };
        var kit = new Kit.ModgudProvisioningClient(handler.Client);
        var realm = await kit.ImportRealmAsync(Realm, new Kit.RealmManifest(), TestContext.Current.CancellationToken);

        var dispose = await Record.ExceptionAsync(async () => await realm.DisposeAsync());

        Assert.Null(dispose);
        Assert.Contains(handler.Bodies, b => b.Method == HttpMethod.Delete);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string Body)> Bodies { get; } = [];
        public bool FailApply { get; init; }
        public bool FailDeleteTransport { get; init; }
        public Action? OnApply { get; init; }

        public HttpClient Client => new(this) { BaseAddress = new Uri("http://kit.test/") };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            var path = request.RequestUri!.AbsolutePath;
            Bodies.Add((request.Method, path, body));

            if (request.Method == HttpMethod.Delete)
                return FailDeleteTransport
                    ? throw new HttpRequestException("connection refused (test)")
                    : new HttpResponseMessage(HttpStatusCode.NoContent);
            if (path.EndsWith("/apply", StringComparison.Ordinal))
            {
                OnApply?.Invoke();
                return FailApply
                    ? Json(HttpStatusCode.BadRequest, """{"Error":"Manifest.Invalid","Message":"rejected (test)"}""")
                    : Json(HttpStatusCode.OK, """{"Slug":"kitcontract","PrimaryDomain":"kit.localhost"}""");
            }
            return Json(HttpStatusCode.Created, """{"Slug":"kitcontract"}""");
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
            new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}
