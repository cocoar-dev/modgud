using System.Diagnostics;

namespace Cocoar.Auth.Infrastructure.Observability;

/// <summary>
/// Shared <see cref="ActivitySource"/> for Cocoar.Auth domain spans.
/// Registered with OpenTelemetry via <c>.AddSource(CocoarAuthActivitySources.Name)</c>.
///
/// <para>Phase 3 establishes the source; concrete spans are added incrementally
/// at the sites that warrant them (long-running flows where the auto-generated
/// AspNetCore + Npgsql spans don't tell the full story — e.g. external-IdP
/// federation, DCR pipeline, GDPR erase).</para>
/// </summary>
public static class CocoarAuthActivitySources
{
    public const string Name = "Cocoar.Auth";

    public static readonly ActivitySource Source = new(Name);
}
