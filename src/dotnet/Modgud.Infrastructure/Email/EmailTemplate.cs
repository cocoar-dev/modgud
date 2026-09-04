namespace Modgud.Infrastructure.Email;

public enum EmailTemplate
{
    EmailOtp,
    MagicLink,
    PasswordReset,
    EmailVerification,
    AdminChangeRequestNotification,
    ChangeRequestApproved,
    ChangeRequestRejected,
    /// <summary>
    /// Bootstrap-invite for the first admin in a freshly provisioned realm
    /// (C15). Recipient clicks the link, lands on /bootstrap?token=...,
    /// sets a password, becomes the realm admin.
    /// </summary>
    RealmAdminBootstrap,
    /// <summary>
    /// ADR 0008 — "sign-in attempts were blocked; if that was you, use this link".
    /// Sent once per throttle window when the user's untrusted failure bucket trips.
    /// The link is a magic-link sign-in that trusts the device on success.
    /// </summary>
    LoginBlocked,
}
