using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BuildingBlocks.Helper;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Authentication.Domain;
using Modgud.Infrastructure.Email;

namespace Modgud.Api.Tests.Security;

/// <summary>
/// B4-AP-03 — the verify-token lifecycle of a pending e-mail change: an expired link
/// for an unchanged address is issued anew, a verified address stays verified when
/// another field is edited, and an empty address (which no link can ever verify) is
/// refused instead of being parked as a request.
/// </summary>
public partial class ProfileSelfServiceTests
{
    private InMemoryEmailService Mails => Factory.Services.GetRequiredService<InMemoryEmailService>();

    [Fact]
    public async Task An_expired_link_is_issued_anew_when_the_same_address_is_submitted_again()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Expired", lastname: "Link", acronym: "EL",
            email: "el-old@test.com", password: "TestPass1234");
        var client = await CreateAuthenticatedClientAsync("el", "TestPass1234");

        await client.PutAsJsonAsync("/api/account/profile/request", new { Email = "el-new@test.com" }, ct);
        var requestId = await GetOpenRequestIdAsync(user.Id);
        await using (var qs = GetTenantedDocumentSession())
        {
            var request = await qs.LoadAsync<UserChangeRequest>(ShortGuid.Decode(requestId), ct);
            request!.VerificationExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            qs.Store(request);
            await qs.SaveChangesAsync(ct);
        }
        Mails.Clear();

        await client.PutAsJsonAsync("/api/account/profile/request", new { Email = "el-new@test.com" }, ct);

        var mail = Mails.GetLastEmailTo("el-new@test.com");
        Assert.NotNull(mail);
        var verify = await Factory.CreateClient().PostAsJsonAsync("/api/account/profile/request/verify-email",
            new { RequestId = requestId, Token = ExtractTokenFromLink(mail!.HtmlBody) }, ct);
        Assert.Equal(HttpStatusCode.OK, verify.StatusCode);
    }

    [Fact]
    public async Task A_verified_address_stays_verified_when_another_field_is_edited()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Stays", lastname: "Verified", acronym: "SV",
            email: "sv-old@test.com", password: "TestPass1234");
        var client = await CreateAuthenticatedClientAsync("sv", "TestPass1234");

        await client.PutAsJsonAsync("/api/account/profile/request", new { Email = "sv-new@test.com" }, ct);
        var requestId = await GetOpenRequestIdAsync(user.Id);
        var token = ExtractTokenFromLink(Mails.GetLastEmailTo("sv-new@test.com")!.HtmlBody);
        var verify = await Factory.CreateClient().PostAsJsonAsync("/api/account/profile/request/verify-email",
            new { RequestId = requestId, Token = token }, ct);
        Assert.Equal(HttpStatusCode.OK, verify.StatusCode);
        Mails.Clear();

        var edit = await client.PutAsJsonAsync("/api/account/profile/request", new { Firstname = "Edited" }, ct);
        var body = await edit.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.Equal("AdminApprovalPending", body.GetProperty("Open").GetProperty("Status").GetString());
        Assert.Null(Mails.GetLastEmailTo("sv-new@test.com"));
    }

    [Fact]
    public async Task Clearing_the_address_is_refused_instead_of_parking_an_unverifiable_request()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Empty", lastname: "Address", acronym: "EA",
            email: "ea@test.com", password: "TestPass1234");
        var client = await CreateAuthenticatedClientAsync("ea", "TestPass1234");

        var r = await client.PutAsJsonAsync("/api/account/profile/request", new { Email = "" }, ct);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);

        await using var qs = GetTenantedSession();
        Assert.False(await qs.Query<UserChangeRequest>().AnyAsync(x => x.UserId == user.Id, ct));
    }
}
