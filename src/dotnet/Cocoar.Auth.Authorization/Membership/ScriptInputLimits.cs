using ErrorOr;

namespace Cocoar.Auth.Authorization.Membership;

/// <summary>
/// Hard caps on the size of admin-authored TypeScript / JavaScript script
/// inputs that flow into the JsEval pipeline. Closes Gap-2 from the
/// JsEval threat model — an unbounded input lets the TS compiler do
/// arbitrary-time work even before the script runs.
///
/// <para>The cap covers <c>Group.MembershipScript</c> and
/// <c>LoginProvider.UserUpdateScript</c>. 16 KiB is comfortably above any
/// realistic predicate or claim-mapping script (existing seed scripts
/// fit in well under 1 KiB) while still bounding compiler work.</para>
/// </summary>
public static class ScriptInputLimits
{
    /// <summary>
    /// Maximum allowed source-text size for a single user-authored script.
    /// 16 KiB; characters are counted (one UTF-16 char = one position),
    /// not bytes.
    /// </summary>
    public const int MaxScriptCharacters = 16 * 1024;

    /// <summary>
    /// Validate that <paramref name="script"/> fits within
    /// <see cref="MaxScriptCharacters"/>. Empty / null inputs pass — those
    /// are caught by the call-site's "is required" check, not by the cap.
    /// </summary>
    /// <param name="script">The TS / JS source text.</param>
    /// <param name="errorCode">
    /// The validation error code to return — call site supplies a
    /// domain-specific value (e.g. <c>"Group.MembershipScriptTooLong"</c>).
    /// </param>
    public static Error? Validate(string? script, string errorCode)
    {
        if (string.IsNullOrEmpty(script)) return null;
        if (script.Length <= MaxScriptCharacters) return null;
        return Error.Validation(
            errorCode,
            $"Script source must not exceed {MaxScriptCharacters:N0} characters " +
            $"(got {script.Length:N0}).");
    }
}
