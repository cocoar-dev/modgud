namespace Cocoar.Auth.Api;

/// <summary>
/// Control-Plane configuration (C14). Identifies the hostnames that serve
/// the cross-realm administration surface: realm CRUD, the first-run setup
/// wizard, and any other deployment-global operation.
///
/// <para>The list is the *expected* contract — at boot, Program.cs validates
/// that every entry resolves to the realm flagged
/// <see cref="Domain.Realms.Realm.IsControlPlane"/>=true, and aborts the
/// host start when the lists disagree (Production only). Dev defaults to
/// trusting the system realm's own <c>Domains</c> so a fresh checkout boots
/// without ENV setup.</para>
///
/// <para>Bound from configuration files + ENV via Cocoar.Configuration v5
/// under the <c>ControlPlane</c> section. ENV override:
/// <c>ControlPlane__Hostnames=auth.example.com,admin.example.com</c>.</para>
/// </summary>
public sealed class ControlPlaneSettings
{
    /// <summary>
    /// Hostnames that serve the Control-Plane surface. Empty in Development
    /// (defaults to the system realm's domains); required in Production
    /// because the routing-gate must know which hosts are global-admin.
    /// </summary>
    public string[] Hostnames { get; set; } = [];
}
