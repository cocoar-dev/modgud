namespace Modgud.Api.Middleware;

/// <summary>
/// Owns the path classification shared by the realm resolver and the
/// realm-independent terminal branch in <c>Program</c>.
/// </summary>
/// <remarks>
/// These are prefixes rather than route segments on purpose: common probes such
/// as <c>/favicon.ico</c>, <c>/swagger.json</c>, and <c>/healthz</c> must stay out
/// of tenant-scoped session/DataProtection even though the suffix does not begin
/// with a slash. Keeping this decision in one place prevents the two pipeline
/// stages from disagreeing and leaving a request without a realm before session.
/// </remarks>
internal static class RealmIndependentPathPolicy
{
    private static readonly string[] Prefixes =
    [
        "/health",
        "/swagger",
        "/openapi",
        "/_framework",
        "/api/install",
        "/install",
        "/assets",
        "/favicon",
    ];

    internal static bool Matches(PathString path)
    {
        var value = path.Value;
        return value is not null
            && Prefixes.Any(prefix => value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The installation UI is the only realm-independent client-side route.
    /// Other prefix matches must not be turned into a successful SPA fallback.
    /// </summary>
    internal static bool AllowsSpaFallback(PathString path) =>
        path.StartsWithSegments("/install", StringComparison.OrdinalIgnoreCase);

    internal static bool CanExecute(Endpoint? endpoint, PathString path) =>
        endpoint?.RequestDelegate is not null
        && (endpoint.Metadata.GetMetadata<SpaFallbackEndpointMetadata>() is null
            || AllowsSpaFallback(path));
}

/// <summary>
/// Marks the catch-all endpoint so realm-independent infrastructure paths can
/// reject scanner probes instead of serving the SPA shell with HTTP 200.
/// </summary>
internal sealed class SpaFallbackEndpointMetadata
{
    internal static SpaFallbackEndpointMetadata Instance { get; } = new();

    private SpaFallbackEndpointMetadata() { }
}
