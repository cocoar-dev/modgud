namespace Cocoar.Auth.Authentication.Configuration;

/// <summary>
/// First-run setup-token gate (SETUP-01). Production deployments require a
/// valid token on <c>POST /api/setup/create-admin</c> so that an attacker
/// who reaches the endpoint before the legitimate operator cannot
/// race-create the initial admin account. Implementation lives in
/// <c>Cocoar.Auth.Api</c>; the interface is shared here so the endpoint
/// (which lives in <c>Cocoar.Auth.Authentication</c>) can resolve it
/// optionally from DI.
/// </summary>
public interface ISetupTokenService
{
    /// <summary>
    /// Whether this environment enforces the setup-token check. False in
    /// Development (so the existing E2E setup-wizard flow keeps working),
    /// true everywhere else.
    /// </summary>
    bool IsRequiredForCurrentEnvironment { get; }

    /// <summary>Filesystem path the operator should read the token from.</summary>
    string TokenFilePath { get; }

    /// <summary>
    /// Validates the presented token (typically from the <c>X-Setup-Token</c>
    /// request header) against the on-disk token file. Returns false if the
    /// file is missing, empty, unreadable, or doesn't match.
    /// </summary>
    bool ValidatePresentedToken(string? presented);

    /// <summary>
    /// Deletes the token file. Called after successful admin creation so a
    /// second setup attempt cannot succeed with the same token even before
    /// the "admin already exists" gate kicks in.
    /// </summary>
    void ConsumeToken();

    /// <summary>
    /// Generates a fresh random token and writes it to <see cref="TokenFilePath"/>
    /// if the file doesn't already exist. Idempotent. Returns true if a new
    /// token was written.
    /// </summary>
    bool TryGenerateIfMissing();
}
