using Modgud.Authentication.Api.Account;
using Modgud.Domain.Applications;
using Action = Modgud.Authentication.Api.Account.NativeOtpEndpoints.NativeOtpAction;

namespace Modgud.Tests.Unit.Authorization;

/// <summary>
/// ADR-0011 Phase 5 — pins the native OTP routing matrix: login for a known
/// confirmed user; JIT create/resend only under the JitOnOtp posture; and the
/// security invariant that a password-bearing unconfirmed account (a pending web
/// registration) is NEVER served a native code.
/// </summary>
public class NativeOtpDecisionTests
{
    [Fact]
    public void Known_confirmed_user_logs_in_regardless_of_posture()
    {
        Assert.Equal(Action.Login,
            NativeOtpEndpoints.Decide(userExists: true, emailConfirmed: true, hasPassword: true, posture: null));
        Assert.Equal(Action.Login,
            NativeOtpEndpoints.Decide(userExists: true, emailConfirmed: true, hasPassword: false, posture: SelfRegPosture.Off));
    }

    [Fact]
    public void Unknown_email_creates_and_registers_only_under_jit()
    {
        Assert.Equal(Action.CreateAndRegister,
            NativeOtpEndpoints.Decide(userExists: false, emailConfirmed: false, hasPassword: false, posture: SelfRegPosture.JitOnOtp));
        Assert.Equal(Action.None,
            NativeOtpEndpoints.Decide(userExists: false, emailConfirmed: false, hasPassword: false, posture: SelfRegPosture.Off));
        Assert.Equal(Action.None,
            NativeOtpEndpoints.Decide(userExists: false, emailConfirmed: false, hasPassword: false, posture: SelfRegPosture.ExplicitEndpoint));
        Assert.Equal(Action.None,
            NativeOtpEndpoints.Decide(userExists: false, emailConfirmed: false, hasPassword: false, posture: null));
    }

    [Fact]
    public void Passwordless_unconfirmed_user_resends_only_under_jit()
    {
        Assert.Equal(Action.ResendRegistration,
            NativeOtpEndpoints.Decide(userExists: true, emailConfirmed: false, hasPassword: false, posture: SelfRegPosture.JitOnOtp));
        Assert.Equal(Action.None,
            NativeOtpEndpoints.Decide(userExists: true, emailConfirmed: false, hasPassword: false, posture: SelfRegPosture.Off));
    }

    [Fact]
    public void Password_bearing_unconfirmed_user_is_never_served_a_native_code()
    {
        // Pending web-registration (has a password, not yet verified) must use the
        // web verification link — never a native OTP, even under JIT.
        Assert.Equal(Action.None,
            NativeOtpEndpoints.Decide(userExists: true, emailConfirmed: false, hasPassword: true, posture: SelfRegPosture.JitOnOtp));
        Assert.Equal(Action.None,
            NativeOtpEndpoints.Decide(userExists: true, emailConfirmed: false, hasPassword: true, posture: SelfRegPosture.Off));
    }
}
