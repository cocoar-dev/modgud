using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using BuildingBlocks.Helper;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Authentication.Domain;
using Modgud.Infrastructure.Email;

namespace Modgud.Api.Tests.Users;

/// <summary>
/// AREG-06 — the account e-mail verification challenge held the user id but not the
/// address it was sent to. When an admin changed the still unconfirmed address from A to B,
/// the old link to A confirmed B without anyone proving control of B.
/// </summary>
public partial class AccountLifecycleTests
{
    [Fact]
    public async Task A_verification_link_does_not_confirm_an_address_changed_since_it_was_sent()
    {
        var ct = TestContext.Current.CancellationToken;
        var userName = $"vfy{Guid.NewGuid():N}"[..12];
        var addressA = $"{userName}-a@test.com";
        var addressB = $"{userName}-b@test.com";
        var user = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Ver", lastname: "Ify", acronym: userName, email: addressA,
            password: DefaultPassword, isRealmAdmin: false);
        await using (var unconfirm = GetTenantedDocumentSession())
        {
            var appUser = (await unconfirm.LoadAsync<ApplicationUser>(user.Id, ct))!;
            appUser.EmailConfirmed = false;
            unconfirm.Store(appUser);
            await unconfirm.SaveChangesAsync(ct);
        }

        using (var browser = await CreateAuthenticatedClientAsync(userName, DefaultPassword))
        using (var sent = await browser.PostAsync("/api/account/email/send-verification", null, ct))
            Assert.True(sent.IsSuccessStatusCode, await sent.Content.ReadAsStringAsync(ct));
        var mail = Factory.Services.GetRequiredService<InMemoryEmailService>().GetLastEmailTo(addressA);
        Assert.NotNull(mail);
        var token = Uri.UnescapeDataString(Regex.Match(mail!.HtmlBody, @"type=account&(?:amp;)?token=([^""&<\s]+)").Groups[1].Value);
        Assert.False(string.IsNullOrEmpty(token));

        using (var changed = await Client.PutAsJsonAsync($"/api/user/{new ShortGuid(user.Id)}", new { Email = addressB }, ct))
            Assert.True(changed.IsSuccessStatusCode, await changed.Content.ReadAsStringAsync(ct));

        using var anonymous = Factory.CreateClient();
        using var verify = await anonymous.PostAsJsonAsync("/api/account/email/verify", new { Token = token }, ct);
        Assert.Equal(HttpStatusCode.BadRequest, verify.StatusCode);

        await using var query = GetTenantedSession();
        var after = (await query.LoadAsync<ApplicationUser>(user.Id, ct))!;
        Assert.Equal(addressB, after.Email);
        Assert.False(after.EmailConfirmed, "a link sent to A must not confirm B");
    }
}
