using Modgud.Authentication;

namespace Modgud.Api;

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
    /// The Page-Builder, editor AND runtime. While off (default): the sidebar
    /// entry is hidden, /admin/customization/pages routes redirect away,
    /// /api/admin/customization/pages/* returns 404, and RealmSettingsDto omits
    /// the Pages section. While on: the editor mounts and persists schemas, and
    /// the auth surfaces render an activated schema instead of their built-in
    /// layout (LoginView checks the same flag before it reads Pages.login).
    ///
    /// A stored schema is bypassed by ?safemode=1 on the login page, and a
    /// schema that fails to parse falls back to the built-in layout rather than
    /// taking authentication down with it.
    /// </summary>
    public bool PageBuilder { get; set; } = false;

    /// <summary>
    /// The function-terminals surface (MG-FT work-item series). While off
    /// (default): the admin sidebar entry is hidden and /api/function/*
    /// returns 404. The principal type itself stays registered — existing
    /// documents remain readable — but nothing can be created or staffed
    /// through the UI/API until an operator turns the feature on.
    /// </summary>
    public bool FunctionTerminals { get; set; } = false;
}
