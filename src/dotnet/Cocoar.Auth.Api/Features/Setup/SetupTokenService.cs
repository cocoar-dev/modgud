using System.Security.Cryptography;
using Cocoar.Auth.Authentication.Configuration;

namespace Cocoar.Auth.Api.Features.Setup;

/// <summary>
/// First-run setup-token mechanism. Closes <c>SETUP-01</c>: in a Production
/// deployment, anyone who reaches <c>/api/setup/create-admin</c> before the
/// legitimate operator could otherwise race-create the admin account. With
/// this gate, only someone who can read a file on the IdP host filesystem
/// can run setup — typically the operator who deployed the container.
///
/// <para>Lifecycle:</para>
/// <list type="number">
///   <item><description>On startup in non-Development, if no admin exists
///   yet AND the token file doesn't exist: generate a fresh random token,
///   write it to <see cref="ResolveTokenPath"/>, log the path + token to
///   stdout/Serilog so the operator can grab it.</description></item>
///   <item><description><c>/api/setup/create-admin</c> requires the
///   <c>X-Setup-Token</c> header to byte-equal the file's content (in
///   Production only — Dev keeps the no-token wizard for E2E ease).</description></item>
///   <item><description>On successful admin creation, the file is deleted.
///   Subsequent setup attempts hit the "admin already exists" gate
///   regardless.</description></item>
/// </list>
///
/// <para>Token path defaults to <c>data/setup-token.txt</c>; override with
/// the <c>Setup__TokenPath</c> env var.</para>
/// </summary>
public sealed class SetupTokenService : Cocoar.Auth.Authentication.Configuration.ISetupTokenService
{
    private readonly IWebHostEnvironment _env;
    private readonly IConfiguration _config;
    private readonly ILogger<SetupTokenService> _logger;

    public SetupTokenService(
        IWebHostEnvironment env,
        IConfiguration config,
        ILogger<SetupTokenService> logger)
    {
        _env = env;
        _config = config;
        _logger = logger;
    }

    public bool IsRequiredForCurrentEnvironment => !_env.IsDevelopment();

    public string TokenFilePath => ResolveTokenPath();

    public bool ValidatePresentedToken(string? presented)
    {
        if (string.IsNullOrEmpty(presented)) return false;

        var path = ResolveTokenPath();
        if (!File.Exists(path)) return false;

        string stored;
        try
        {
            stored = File.ReadAllText(path).Trim();
        }
        catch
        {
            return false;
        }

        if (string.IsNullOrEmpty(stored)) return false;

        // Constant-time compare to keep the file-content equality check
        // resilient against timing-style probing.
        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(stored),
            System.Text.Encoding.UTF8.GetBytes(presented));
    }

    public void ConsumeToken()
    {
        var path = ResolveTokenPath();
        if (!File.Exists(path)) return;
        try
        {
            File.Delete(path);
            _logger.LogInformation("Setup: token file consumed and deleted at {Path}", path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Setup: could not delete token file at {Path}", path);
        }
    }

    public bool TryGenerateIfMissing()
    {
        var path = ResolveTokenPath();
        if (File.Exists(path))
        {
            _logger.LogInformation("Setup: token file already exists at {Path}; leaving in place.", path);
            return false;
        }

        var bytes = RandomNumberGenerator.GetBytes(32);
        var token = Base64UrlEncode(bytes);

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(path, token);
        try
        {
            // Best-effort: tighten file permissions on POSIX. Windows ACLs
            // are managed by the deploy environment, not here.
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        catch
        {
            // Permissions are best-effort; the token's value still requires
            // local FS access regardless.
        }

        _logger.LogWarning(
            "Setup: generated first-run setup token at {Path}. " +
            "Use it as the X-Setup-Token header on POST /api/setup/create-admin. " +
            "Token: {Token}",
            path, token);

        return true;
    }

    private string ResolveTokenPath()
    {
        var configured = _config["Setup:TokenPath"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.IsPathRooted(configured)
                ? configured
                : Path.Combine(_env.ContentRootPath, configured);
        }
        return Path.Combine(_env.ContentRootPath, "data", "setup-token.txt");
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}

