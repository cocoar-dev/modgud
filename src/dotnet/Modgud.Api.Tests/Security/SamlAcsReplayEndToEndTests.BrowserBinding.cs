using System.Net;

namespace Modgud.Api.Tests.Security;

/// <summary>
/// B6-SAML-01 — the pending AuthnRequest was single-use but not bound to the browser that
/// started it. An attacker could start a login in their own browser, obtain the IdP's
/// signed Response for their own identity and submit it from the victim's browser within
/// the 15-minute window: login CSRF / session swapping, the victim silently working in
/// the attacker's account. The login start now sets a browser binding cookie
/// (SameSite=None, Secure on HTTPS) whose hash the pending request records; the ACS
/// refuses a Response presented without the matching cookie.
/// </summary>
public partial class SamlAcsReplayEndToEndTests
{
    [Fact]
    public async Task A_response_started_in_another_browser_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        using var idpCert = CreateIdpSigningCert();
        var provider = await CreateAndRegisterProviderAsync(idpCert);
        await SeedLinkedUserAsync(provider.Id);

        // The attacker starts the login and gets the IdP's answer for their identity.
        using var attacker = NewBrowser();
        var requestId = await StartRequestAsync(attacker, provider.Id, ct);
        var response = MintSignedResponse(idpCert, requestId);

        // Submitted from a victim browser that never started this login.
        using var victim = NewBrowser();
        var swapped = await PostToAcsAsync(victim, response, ct);
        Assert.Equal(HttpStatusCode.Redirect, swapped.StatusCode);
        Assert.Contains("error=saml-browser-mismatch", swapped.Headers.Location!.ToString());
        Assert.False(HasApplicationCookie(swapped));
        Assert.Equal(HttpStatusCode.Unauthorized, (await victim.GetAsync("/api/account/me", ct)).StatusCode);
        Assert.False((await LoadPendingRequestAsync(requestId, ct))!.IsConsumed);

        // Even a victim who started a SAML login of their own holds a different binding.
        await StartRequestAsync(victim, provider.Id, ct);
        var stillSwapped = await PostToAcsAsync(victim, response, ct);
        Assert.Contains("error=saml-browser-mismatch", stillSwapped.Headers.Location!.ToString());

        // The browser that started the login still completes it.
        var own = await PostToAcsAsync(attacker, response, ct);
        Assert.Equal("/", own.Headers.Location!.ToString());
        Assert.True(HasApplicationCookie(own));
    }
}
