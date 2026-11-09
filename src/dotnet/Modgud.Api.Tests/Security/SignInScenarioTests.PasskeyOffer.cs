using System.Net;
using System.Net.Http.Json;
using Modgud.Application.DTOs.RealmSettings;
using Modgud.Authentication.SignIn;
using Modgud.Domain.Realms;

namespace Modgud.Api.Tests.Security;

/// <summary>
/// B8-AS04 — password and e-mail code sign-ins are refused on the server when the sign-in offer
/// switches them off; a web passkey sign-in was not: with SignIn.Passkey = false the login options
/// were still issued and the passkey login still succeeded. Confirming the account (re-authentication)
/// is not a sign-in and stays possible.
/// </summary>
public partial class SignInScenarioTests
{
    [Fact]
    public async Task Passkey_login_options_are_refused_when_the_offer_switches_passkeys_off()
    {
        await RealmPolicyAsync(new UpdateSignInPolicyDto { Passkey = false });

        using var resp = await Factory.CreateClient().PostAsJsonAsync("/api/account/passkey/login-options", new { }, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    /// <summary>EG-07 — max_age was measured from the cookie's IssuedUtc, which a sliding renewal
    /// moves forward without any new sign-in: a sign-in proven long ago passed a short max_age. It is
    /// now measured from the latest proven factor, the same time the tokens carry as auth_time.</summary>
    [Fact]
    public async Task Max_age_is_measured_from_the_latest_proven_factor_not_the_renewed_cookie()
    {
        var clientId = await CreateAppClientAsync(SignInLevel.Single);

        var stale = await SessionAsync(recorded: true, TimeSpan.FromMinutes(1), (SignInMethods.Password, TimeSpan.FromHours(2)));
        var staleLocation = await AuthorizeLocationAsync(stale, clientId, "max_age=600");
        Assert.False(staleLocation?.StartsWith(RedirectUri, StringComparison.Ordinal) == true,
            $"a sign-in proven two hours ago passed max_age=600: {staleLocation}");

        var fresh = await SessionAsync(recorded: true, TimeSpan.FromMinutes(1), (SignInMethods.Password, TimeSpan.FromMinutes(1)));
        Assert.StartsWith(RedirectUri, await AuthorizeLocationAsync(fresh, clientId, "max_age=600"));
    }
}
