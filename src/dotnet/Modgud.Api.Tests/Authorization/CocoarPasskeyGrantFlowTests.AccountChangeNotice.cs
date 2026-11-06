using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Infrastructure.Email;

namespace Modgud.Api.Tests.Authorization;

/// <summary>
/// RV-0001 stage 1 — the browser mails an account-change notice when a passkey is added or removed;
/// the native endpoints changed the account's factors silently. They now send the same notice. (Stage 1
/// also logs a warning when the change would need a fresh sign-in in the browser; that is a log, not a
/// refusal, and not asserted here.)
/// </summary>
public partial class CocoarPasskeyGrantFlowTests
{
    [Fact]
    public async Task Native_passkey_enrollment_mails_the_account_change_notice()
    {
        await EnableNativeGrantsAsync();
        var appId = await SeedNativeAppAsync([ListedAndroidOrigin]);
        await SeedPasskeyClientAsync("notice-enroll", rpId: AndroidRpId, appId: appId);
        var bearer = await BearerForAsync("notice-enroll");
        var start = DateTimeOffset.UtcNow;

        var (status, body, _) = await EnrollAsync(bearer, ListedAndroidOrigin);
        Assert.True(status is >= 200 and < 300, $"enroll failed ({status}): {body}");

        Assert.True(NoticeSentSince(start), "adding a passkey through the native endpoint sent no account-change notice");
    }

    [Fact]
    public async Task Native_passkey_deletion_mails_the_account_change_notice()
    {
        await EnableNativeGrantsAsync();
        var token = await MintAccessTokenAsync();
        var ownId = await SeedOwnedCredentialAsync(DefaultUser!.Id, "Old Phone");
        var start = DateTimeOffset.UtcNow;

        var resp = await BearerClient(token).DeleteAsync($"/connect/passkey/{ownId}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);

        Assert.True(NoticeSentSince(start), "removing a passkey through the native endpoint sent no account-change notice");
    }

    private bool NoticeSentSince(DateTimeOffset start) =>
        Factory.Services.GetRequiredService<InMemoryEmailService>().GetSentEmails()
            .Any(m => m.SentAt >= start && string.Equals(m.To, DefaultUser!.Email, StringComparison.OrdinalIgnoreCase));
}
