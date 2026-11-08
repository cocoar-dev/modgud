using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Modgud.Api.Features.Auth;
using Modgud.Authentication.Api.Account;
using Modgud.Authentication.Domain;
using Modgud.Domain.Realms;

namespace Modgud.Tests.Unit.Api.Features;

/// <summary>
/// RV-0001 stage 2 — a native passkey change needs a sign-in from the last 15 minutes once the
/// realm (or App) switches it on; otherwise the call is refused with RFC 9470's
/// <c>insufficient_user_authentication</c> and <c>max_age</c>, and the app signs the user in
/// again. Any sign-in the app allows counts, an e-mail code included: that is the way back for
/// a user whose passkey device is gone. Switched off, the change goes ahead as before.
/// </summary>
public class NativeRecentSignInTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public void A_sign_in_within_15_minutes_is_recent_and_none_or_an_older_one_is_not()
    {
        Assert.True(NativeAccountChange.IsRecent(Now.AddMinutes(-14), Now));
        Assert.False(NativeAccountChange.IsRecent(Now.AddMinutes(-16), Now));
        Assert.False(NativeAccountChange.IsRecent(null, Now));
    }

    [Fact]
    public async Task Switched_on_a_stale_sign_in_is_refused_with_insufficient_user_authentication()
    {
        var refusal = NativeAccountChange.CheckRecentSignIn(
            Caller(Now.AddHours(-2)), Enforced(true), NullLogger.Instance, new ApplicationUser(), "app",
            AccountChange.PasskeyAdded, log: true);

        Assert.NotNull(refusal);
        var http = new DefaultHttpContext();
        http.Response.Body = new MemoryStream();
        await refusal.ExecuteAsync(http);

        Assert.Equal(StatusCodes.Status401Unauthorized, http.Response.StatusCode);
        var challenge = http.Response.Headers.WWWAuthenticate.ToString();
        Assert.StartsWith("Bearer ", challenge);
        Assert.Contains("error=\"insufficient_user_authentication\"", challenge);
        Assert.Contains("max_age=\"900\"", challenge);

        http.Response.Body.Position = 0;
        using var body = await JsonDocument.ParseAsync(http.Response.Body);
        Assert.Equal("insufficient_user_authentication", body.RootElement.GetProperty("error").GetString());
        Assert.Equal(900, body.RootElement.GetProperty("max_age").GetInt32());
    }

    [Fact]
    public void Switched_on_a_fresh_sign_in_passes()
    {
        Assert.Null(NativeAccountChange.CheckRecentSignIn(
            Caller(Now.AddMinutes(-1)), Enforced(true), NullLogger.Instance, new ApplicationUser(), "app",
            AccountChange.PasskeyRemoved, log: true));
    }

    [Fact]
    public void Switched_off_a_stale_sign_in_still_passes()
    {
        Assert.Null(NativeAccountChange.CheckRecentSignIn(
            Caller(Now.AddHours(-2)), Enforced(false), NullLogger.Instance, new ApplicationUser(), "app",
            AccountChange.PasskeyAdded, log: true));
    }

    [Fact]
    public void A_token_without_auth_time_counts_as_stale()
    {
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity("Bearer")) };
        Assert.NotNull(NativeAccountChange.CheckRecentSignIn(
            http, Enforced(true), NullLogger.Instance, new ApplicationUser(), "app",
            AccountChange.PasskeyAdded, log: false));
    }

    private static NativeGrantSettings Enforced(bool on) =>
        new() { Enabled = true, RequireRecentSignInForPasskeyChanges = on };

    private static DefaultHttpContext Caller(DateTimeOffset authTime) => new()
    {
        User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("auth_time", authTime.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture))],
            "Bearer")),
    };
}
