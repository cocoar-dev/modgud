using Cocoar.Auth.Authentication;

namespace Cocoar.Auth.Api;

/// <summary>
/// Application settings — loaded from configuration files at startup.
/// Controls authentication enforcement level and magic link self-service.
/// </summary>
public class AppSettings : IAuthSettings
{
    /// <summary>
    /// 0 = None (password-only allowed), 1 = SecureLogin (default, password-only blocked),
    /// 2 = Passwordless (password login disabled entirely)
    /// </summary>
    public int AuthenticationMinimumLevel { get; set; } = 1;

    /// <summary>
    /// Whether users can request magic links themselves from the login page.
    /// Admin-send magic links are always available regardless of this setting.
    /// </summary>
    public bool MagicLinkSelfService { get; set; } = true;

    /// <summary>
    /// At AuthenticationMinimumLevel >= 1, users without any 2FA method have this many days
    /// after their first post-enforcement login to set one up. During the grace period the
    /// login succeeds with a RequiresSecureSetup flag but the frontend allows postponing.
    /// After expiry, login still succeeds (cookie is set) but the setup modal becomes blocking.
    /// </summary>
    public int TwoFactorGracePeriodDays { get; set; } = 14;

    /// <summary>
    /// Operator-level feature toggles. System-wide (not per-tenant), set in
    /// configuration.json / ENV-overrides. Use for shipping work-in-progress
    /// surfaces dark until they're ready to expose to tenant admins.
    /// </summary>
    public FeatureFlags Features { get; set; } = new();
}

public class FeatureFlags : IFeatureFlags
{
    /// <summary>
    /// Visibility of the Page-Builder editor in Admin → Customization → Pages.
    /// While off (default): sidebar entry hidden, /admin/customization/pages
    /// routes redirect away, /api/admin/customization/pages/* returns 404,
    /// and RealmSettingsDto omits the Pages section. While on: editor mounts
    /// and persists schemas. Runtime rendering of those schemas on /login etc.
    /// is a separate sprint and not gated by this flag.
    /// </summary>
    public bool PageBuilder { get; set; } = false;
}
