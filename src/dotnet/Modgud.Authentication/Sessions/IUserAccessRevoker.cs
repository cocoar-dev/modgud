namespace Modgud.Authentication.Sessions;

/// <summary>Why a user's live access is being revoked — drives whether consent
/// grants are also dropped and feeds the audit log.</summary>
public enum AccessRevocationReason
{
    /// <summary>Reversible deactivation. Kills tokens + sessions + cookies but
    /// KEEPS authorizations (consent grants), so reactivation doesn't force the
    /// user back through the consent screen.</summary>
    Deactivation,

    /// <summary>Irreversible removal (admin delete or GDPR erase). Revokes
    /// everything, including consent grants.</summary>
    Deletion,

    /// <summary>Admin-initiated "log this user out everywhere". Like
    /// <see cref="Deactivation"/> — kills live access but keeps consent and
    /// leaves the account active so the user can log back in.</summary>
    ForceSignOut,
}

/// <summary>
/// The user-lifecycle access "kill switch". Revokes a user's live access
/// across every channel: OpenIddict tokens (immediate, reference tokens),
/// optionally OpenIddict authorizations, device-session rows, and the security
/// stamp (which invalidates auth cookies at the next SecurityStampValidator
/// pass and fails the refresh-token parity check).
/// <para>
/// Call this BEFORE soft-deleting the user document — once <c>IsDeleted</c>
/// flips, <see cref="Microsoft.AspNetCore.Identity.UserManager{T}.FindByIdAsync"/>
/// filters the user out and the security-stamp rotation can no longer load it.
/// </para>
/// </summary>
public interface IUserAccessRevoker
{
    Task RevokeAllAccessAsync(Guid userId, AccessRevocationReason reason, CancellationToken ct = default);
}
