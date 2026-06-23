using Modgud.Authentication.Api.Account;
using Modgud.Domain.Applications;
using static Modgud.Authentication.Api.Account.NativeOtpEndpoints;

namespace Modgud.Api.Tests.Authorization;

/// <summary>
/// Pure unit pins for <see cref="NativeOtpEndpoints.Decide"/> (ADR-0010 login +
/// ADR-0011 JIT registration + ADR-0012 invite-code registration). No DB: this
/// pins the posture/email-state routing only. The invite-code <em>validity</em>
/// gate (which needs the DB) is exercised end-to-end in
/// <see cref="InviteCodeRegistrationFlowTests"/>.
/// </summary>
public class NativeOtpDecisionTests
{
    [Theory]
    // Confirmed user → always a plain login, regardless of posture.
    [InlineData(true, true, false, SelfRegPosture.Off, NativeOtpAction.Login)]
    [InlineData(true, true, false, SelfRegPosture.JitOnOtp, NativeOtpAction.Login)]
    [InlineData(true, true, false, SelfRegPosture.InviteCode, NativeOtpAction.Login)]
    // Unconfirmed passwordless in-progress sign-up → resend under self-reg postures.
    [InlineData(true, false, false, SelfRegPosture.JitOnOtp, NativeOtpAction.ResendRegistration)]
    [InlineData(true, false, false, SelfRegPosture.InviteCode, NativeOtpAction.ResendRegistration)]
    [InlineData(true, false, false, SelfRegPosture.Off, NativeOtpAction.None)]
    // Unconfirmed but password-bearing → never served a native code.
    [InlineData(true, false, true, SelfRegPosture.JitOnOtp, NativeOtpAction.None)]
    [InlineData(true, false, true, SelfRegPosture.InviteCode, NativeOtpAction.None)]
    // Unknown email → create only under a self-reg posture (InviteCode still needs
    // a valid code downstream, but routes to CreateAndRegister here).
    [InlineData(false, false, false, SelfRegPosture.JitOnOtp, NativeOtpAction.CreateAndRegister)]
    [InlineData(false, false, false, SelfRegPosture.InviteCode, NativeOtpAction.CreateAndRegister)]
    [InlineData(false, false, false, SelfRegPosture.ExplicitEndpoint, NativeOtpAction.None)]
    [InlineData(false, false, false, SelfRegPosture.Off, NativeOtpAction.None)]
    public void Decide_Routes_Per_Posture_And_EmailState(
        bool userExists, bool emailConfirmed, bool hasPassword, SelfRegPosture posture, NativeOtpAction expected)
    {
        Assert.Equal(expected, Decide(userExists, emailConfirmed, hasPassword, posture));
    }

    [Fact]
    public void Decide_NullPosture_Unknown_Email_Is_None()
    {
        Assert.Equal(NativeOtpAction.None, Decide(userExists: false, emailConfirmed: false, hasPassword: false, posture: null));
    }
}
