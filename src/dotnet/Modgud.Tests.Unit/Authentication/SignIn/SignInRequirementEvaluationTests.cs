using Modgud.Authentication.SignIn;
using Modgud.Domain.Applications;
using Modgud.Domain.Realms;
using Facts = Modgud.Authentication.SignIn.SignInRequirementService.SignInFacts;

namespace Modgud.Tests.Unit.Authentication.SignIn;

/// <summary>
/// ADR 0025 — the rule that decides what a sign-in must reach: the higher of the target
/// App's minimum and the second factor the user switched on themselves (where the App
/// honours it). Pure evaluation, no storage.
/// </summary>
public class SignInRequirementEvaluationTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static Facts User(
        bool totp = false, bool emailSecondFactor = false, bool usablePasskey = false,
        bool anySecondFactor = false, bool exempt = false, DateTimeOffset? dueAt = null) =>
        new(totp, emailSecondFactor, usablePasskey, anySecondFactor || totp || emailSecondFactor || usablePasskey, exempt, dueAt);

    private static IReadOnlySet<string> Proven(params string[] methods) => methods.ToHashSet(StringComparer.Ordinal);

    private static SignInDecision Evaluate(SignInPolicy policy, Facts user, IReadOnlySet<string> proven,
        SignInSurface surface = SignInSurface.Web) =>
        SignInRequirementService.Evaluate(policy, user, proven, surface, Now);

    private static readonly SignInPolicy CodeOnlyApp = SignInPolicy.Defaults with
    {
        Password = false, EmailCode = true, Passkey = false, Totp = false, EmailAfterPassword = false,
        OwnFactorNotOffered = OwnFactorNotOffered.Ignore,
    };

    [Fact]
    public void An_e_mail_code_is_enough_for_a_single_level_app()
    {
        var decision = Evaluate(CodeOnlyApp, User(), Proven(SignInMethods.Email));

        Assert.Equal(SignInOutcome.Satisfied, decision.Outcome);
        Assert.False(decision.SetupPending);
    }

    [Fact]
    public void A_passkey_enrolled_by_a_native_app_never_blocks_an_e_mail_code_sign_in()
    {
        // The reported case: the passkey belongs to the App's RP ID, the login page cannot
        // use it, and the user never switched on a second factor.
        var decision = Evaluate(SignInPolicy.Defaults with { EmailCode = true },
            User(anySecondFactor: true, usablePasskey: false), Proven(SignInMethods.Email));

        Assert.Equal(SignInOutcome.Satisfied, decision.Outcome);
    }

    [Fact]
    public void The_users_own_totp_is_asked_for_where_the_app_offers_it()
    {
        var decision = Evaluate(SignInPolicy.Defaults with { EmailCode = true }, User(totp: true), Proven(SignInMethods.Email));

        Assert.Equal(SignInOutcome.NeedSecondFactor, decision.Outcome);
        Assert.Equal([SignInMethods.Totp], decision.SecondFactors);
    }

    [Fact]
    public void The_users_own_totp_is_ignored_by_an_app_that_neither_offers_nor_requires_it()
    {
        var decision = Evaluate(CodeOnlyApp, User(totp: true), Proven(SignInMethods.Email), SignInSurface.Native);

        Assert.Equal(SignInOutcome.Satisfied, decision.Outcome);
    }

    [Fact]
    public void An_app_that_does_not_offer_totp_natively_sends_the_users_own_totp_to_the_browser()
    {
        var policy = CodeOnlyApp with { OwnFactorNotOffered = OwnFactorNotOffered.RequireViaBrowser };

        var native = Evaluate(policy, User(totp: true), Proven(SignInMethods.Email), SignInSurface.Native);
        var web = Evaluate(policy, User(totp: true), Proven(SignInMethods.Email), SignInSurface.Web);

        Assert.Equal(SignInOutcome.NeedSecondFactor, native.Outcome);
        Assert.Empty(native.SecondFactors);
        Assert.Equal([SignInMethods.Totp], native.BrowserOnlyFactors);
        Assert.Equal([SignInMethods.Totp], web.SecondFactors);
    }

    [Fact]
    public void An_app_that_implements_totp_asks_for_it_natively()
    {
        var policy = CodeOnlyApp with { Totp = true };

        var decision = Evaluate(policy, User(totp: true), Proven(SignInMethods.Email), SignInSurface.Native);

        Assert.Equal([SignInMethods.Totp], decision.SecondFactors);
        Assert.Empty(decision.BrowserOnlyFactors);
    }

    [Fact]
    public void The_e_mail_second_factor_follows_a_password_but_never_an_e_mail_code()
    {
        var user = User(emailSecondFactor: true);

        var afterPassword = Evaluate(SignInPolicy.Defaults, user, Proven(SignInMethods.Password));
        var afterCode = Evaluate(SignInPolicy.Defaults with { EmailCode = true }, user, Proven(SignInMethods.Email));

        Assert.Equal([SignInMethods.Email], afterPassword.SecondFactors);
        Assert.Equal(SignInOutcome.Satisfied, afterCode.Outcome);
    }

    [Fact]
    public void A_multi_app_offers_a_usable_passkey_as_the_second_step()
    {
        var policy = SignInPolicy.Defaults with { MinimumLevel = SignInLevel.Multi, EmailCode = true };

        var decision = Evaluate(policy, User(usablePasskey: true), Proven(SignInMethods.Email));

        Assert.Equal([SignInMethods.Passkey], decision.SecondFactors);
    }

    [Theory]
    [InlineData(SignInMethods.Passkey)]
    [InlineData(SignInMethods.ExternalMfa)]
    public void A_factor_that_is_multi_on_its_own_satisfies_a_multi_app(string method)
    {
        var policy = SignInPolicy.Defaults with { MinimumLevel = SignInLevel.Multi };

        var decision = Evaluate(policy, User(totp: true), Proven(method));

        Assert.Equal(SignInOutcome.Satisfied, decision.Outcome);
        Assert.Equal(SignInLevel.Multi, decision.Achieved);
    }

    [Fact]
    public void A_user_without_a_second_factor_gets_the_setup_grace_then_the_setup_duty()
    {
        var policy = SignInPolicy.Defaults with { MinimumLevel = SignInLevel.Multi };

        var notStarted = Evaluate(policy, User(), Proven(SignInMethods.Password));
        var inGrace = Evaluate(policy, User(dueAt: Now.AddDays(3)), Proven(SignInMethods.Password));
        var overdue = Evaluate(policy, User(dueAt: Now.AddDays(-1)), Proven(SignInMethods.Password));
        var exempt = Evaluate(policy, User(exempt: true, dueAt: Now.AddDays(-1)), Proven(SignInMethods.Password));

        Assert.Equal(SignInOutcome.Satisfied, notStarted.Outcome);
        Assert.True(notStarted.SetupPending);
        Assert.Equal(SignInOutcome.Satisfied, inGrace.Outcome);
        Assert.True(inGrace.SetupPending);
        Assert.Equal(SignInOutcome.SetupRequired, overdue.Outcome);
        Assert.Equal(SignInOutcome.Satisfied, exempt.Outcome);
        Assert.False(exempt.SetupPending);
    }

    [Fact]
    public void Two_codes_over_the_same_channel_are_one_factor()
    {
        Assert.Equal(SignInLevel.Single, SignInAssurance.LevelOf([SignInMethods.Email]));
        Assert.Equal(SignInLevel.Multi, SignInAssurance.LevelOf([SignInMethods.Password, SignInMethods.Email]));
        Assert.Equal(SignInLevel.Multi, SignInAssurance.LevelOf([SignInMethods.Email, SignInMethods.Totp]));
        Assert.Equal(SignInLevel.Single, SignInAssurance.LevelOf([SignInMethods.External]));
    }

    [Fact]
    public void Amr_uses_rfc_8176_values()
    {
        Assert.Equal(["pwd", "otp", "mfa"], SignInAssurance.ToAmr([SignInMethods.Password, SignInMethods.Totp]));
        Assert.Equal(["otp"], SignInAssurance.ToAmr([SignInMethods.Email]));
        Assert.Equal(["hwk", "user", "mfa"], SignInAssurance.ToAmr([SignInMethods.Passkey]));
    }

    [Fact]
    public void Two_apps_in_one_sign_in_combine_to_the_strictest_policy()
    {
        var consumer = CodeOnlyApp;
        var admin = SignInPolicy.Defaults with { MinimumLevel = SignInLevel.Multi, SetupGraceDays = 3 };

        var combined = SignInRequirementService.Strictest(consumer, admin);

        Assert.Equal(SignInLevel.Multi, combined.MinimumLevel);
        Assert.Equal(3, combined.SetupGraceDays);
        Assert.False(combined.Password);
        Assert.Equal(OwnFactorNotOffered.RequireViaBrowser, combined.OwnFactorNotOffered);
    }

    [Theory]
    [InlineData("/connect/authorize?client_id=mcp&resource=https%3A%2F%2Fapi.example%2Fmcp", true, "mcp", 1)]
    [InlineData("/connect/authorize?client_id=a&resource=x&resource=y", true, "a", 2)]
    [InlineData("/admin/users", false, null, 0)]
    [InlineData("https://evil.example/connect/authorize?client_id=a", false, null, 0)]
    [InlineData("//evil.example/connect/authorize?client_id=a", false, null, 0)]
    public void Only_a_local_authorize_continuation_names_a_client_and_resources(
        string returnUrl, bool expected, string? clientId, int resourceCount)
    {
        var parsed = SignInRequirementService.TryParseAuthorize(returnUrl, out var parsedClient, out var resources);

        Assert.Equal(expected, parsed);
        Assert.Equal(clientId, parsedClient);
        Assert.Equal(resourceCount, resources.Count);
    }

    [Theory]
    // page on the app's own domain (or below it): the app's RP ID
    [InlineData("app.example-app.test", "app.example-app.test", false, "app.example-app.test")]
    [InlineData("login.app.example-app.test", "app.example-app.test", false, "app.example-app.test")]
    // Modgud's page elsewhere: only via related origins
    [InlineData("auth.example.org", "app.example-app.test", false, "auth.example.org")]
    [InlineData("auth.example.org", "app.example-app.test", true, "app.example-app.test")]
    // no app RP ID: always the realm's
    [InlineData("auth.example.org", null, true, "auth.example.org")]
    // look-alike host is not "below" the RP ID
    [InlineData("evilapp.example-app.test", "app.example-app.test", false, "auth.example.org")]
    public void The_web_rp_id_is_the_apps_only_where_the_browser_can_use_it(
        string host, string? appRpId, bool relatedOrigins, string expected)
    {
        var rpId = SignInRequirementService.WebRpIdFor(appRpId, "auth.example.org", host, relatedOrigins);

        Assert.Equal(expected, rpId);
    }

    [Fact]
    public void S50_an_unsaved_realm_runs_with_a_single_floor_and_a_multi_administration()
    {
        var withNativeGrants = SignInPolicy.ForUnsavedRealm(emailCodeEnabled: true);
        var withoutNativeGrants = SignInPolicy.ForUnsavedRealm(emailCodeEnabled: false);

        Assert.Equal(SignInLevel.Single, withNativeGrants.MinimumLevel);
        Assert.Equal(SignInLevel.Multi, withNativeGrants.AdministrationMinimumLevel);
        Assert.Equal(14, withNativeGrants.SetupGraceDays);
        Assert.True(withNativeGrants.Password);
        Assert.True(withNativeGrants.Passkey);
        Assert.True(withNativeGrants.EmailCode);
        Assert.False(withoutNativeGrants.EmailCode);
    }

    [Fact]
    public void An_app_only_raises_the_realm_floor()
    {
        var floor = SignInPolicy.Defaults with { MinimumLevel = SignInLevel.Multi, EmailCode = false };
        var below = new ApplicationSignInOverrides { MinimumLevel = SignInLevel.Single, EmailCode = true, Password = false };

        var effective = EffectiveSettings.ApplySignInOverrides(floor, below);

        Assert.Equal(SignInLevel.Multi, effective.MinimumLevel);
        Assert.False(effective.EmailCode);
        Assert.False(effective.Password);
        Assert.Equal(["MinimumLevel", "EmailCode"], EffectiveSettings.BelowFloor(floor, below));
    }
}
