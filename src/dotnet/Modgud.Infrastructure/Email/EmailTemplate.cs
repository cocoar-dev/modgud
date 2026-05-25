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
}
