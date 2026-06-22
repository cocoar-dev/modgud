namespace Modgud.Domain.Realms;

/// <summary>The identity fields whose collection is governed by
/// <see cref="RegistrationFieldsSettings"/>. (Email is the always-required anchor
/// and is validated separately on every creation path.)</summary>
public enum RegistrationField { Username, Firstname, Lastname }

/// <summary>
/// Pure enforcement of <see cref="RegistrationFieldsSettings"/>, shared by every
/// account-creation path (admin-create / -edit, native passwordless OTP +
/// explicit register, web self-registration). Has no dependencies so it lives in
/// the domain; each caller maps the returned <see cref="RegistrationField"/> onto
/// its own error/response shape (a hard <c>400</c> on the admin + native paths, a
/// silent generic response on the anti-enumeration self-registration path).
///
/// <para>A required-field check is independent of whether the email already
/// exists, so callers may surface it BEFORE any uniform/anti-enumeration branch
/// without leaking account existence.</para>
/// </summary>
public static class RegistrationFieldsPolicy
{
    /// <summary>The first required field that is missing (empty/whitespace), or
    /// <c>null</c> when every required field is satisfied. Pass <c>null</c> for a
    /// field a given path never collects (e.g. native passwordless has no separate
    /// username — the username is always the email — so it passes the email as the
    /// username, making a <c>Username = Required</c> policy a non-issue there).</summary>
    public static RegistrationField? FirstMissingRequired(
        RegistrationFieldsSettings? settings,
        string? username,
        string? firstname,
        string? lastname)
    {
        settings ??= RegistrationFieldsSettings.Defaults;

        if (settings.Username == FieldRequirement.Required && string.IsNullOrWhiteSpace(username))
            return RegistrationField.Username;
        if (settings.Firstname == FieldRequirement.Required && string.IsNullOrWhiteSpace(firstname))
            return RegistrationField.Firstname;
        if (settings.Lastname == FieldRequirement.Required && string.IsNullOrWhiteSpace(lastname))
            return RegistrationField.Lastname;
        return null;
    }

    /// <summary>The first required NAME field that is missing — the native
    /// passwordless paths' subset (username is always the email there, so it is
    /// never enforced). <c>null</c> = both satisfied.</summary>
    public static RegistrationField? FirstMissingRequiredName(
        RegistrationFieldsSettings? settings, string? firstname, string? lastname)
        => FirstMissingRequired(settings, username: "n/a", firstname, lastname);

    /// <summary>The username to persist given the policy and the supplied value:
    /// <list type="bullet">
    ///   <item><see cref="FieldRequirement.Off"/> — always the email (no separate
    ///   username is collected).</item>
    ///   <item><see cref="FieldRequirement.Optional"/> — the supplied username, or
    ///   the email when it is empty (today's lenient default).</item>
    ///   <item><see cref="FieldRequirement.Required"/> — the supplied username
    ///   (callers validate non-empty via <see cref="FirstMissingRequired"/> first;
    ///   an empty value still falls back to the email defensively).</item>
    /// </list></summary>
    public static string ResolveUsername(RegistrationFieldsSettings? settings, string? username, string email)
    {
        settings ??= RegistrationFieldsSettings.Defaults;
        if (settings.Username == FieldRequirement.Off)
            return email.Trim();
        return string.IsNullOrWhiteSpace(username) ? email.Trim() : username.Trim();
    }
}
