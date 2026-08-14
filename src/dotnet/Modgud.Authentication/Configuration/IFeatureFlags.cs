namespace Modgud.Authentication;

/// <summary>
/// Operator-level feature toggles, read-only abstraction so the
/// Authentication slice can gate surfaces without taking a direct
/// dependency on the API project's <c>AppSettings</c> class. Concrete
/// implementation lives in <c>AppSettings.FeatureFlags</c>; wired in
/// the Api composition root.
/// </summary>
public interface IFeatureFlags
{
    /// <summary>
    /// Visibility of the Page-Builder editor surface. While off the
    /// admin sidebar entry is hidden, the endpoints under
    /// <c>/api/admin/customization/pages/*</c> return 404, and
    /// <c>RealmSettingsDto.Pages</c> is emitted empty.
    /// </summary>
    bool PageBuilder { get; }

    /// <summary>
    /// Visibility of the position-terminals surface (MG-FT). While off the
    /// admin sidebar entry is hidden and the endpoints under
    /// <c>/api/position/*</c> return 404 — the feature ships dark on
    /// <c>develop</c> until the work-item series is complete.
    /// </summary>
    bool PositionTerminals { get; }
}
