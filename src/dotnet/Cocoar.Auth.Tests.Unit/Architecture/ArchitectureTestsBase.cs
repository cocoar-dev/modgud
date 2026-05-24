using System.Reflection;
using NetArchTest.Rules;

namespace Cocoar.Auth.Tests.Unit.Architecture;

/// <summary>
/// Shared helpers and assembly references for NetArchTest-based slice-boundary
/// rules. Pinning assemblies via <c>typeof(Marker).Assembly</c> avoids
/// hard-coded assembly names and survives renames.
///
/// <para>Pattern mirrors Cocoar.AppBase v2.1.0's Architecture tests
/// (<c>Cocoar.AppBase.Api.Tests/Architecture/</c>). Kept narrower here on
/// purpose: only the rules that are CURRENTLY true for Cocoar.Auth are
/// codified — known inversions (Domain → Authorization project reference)
/// stay out so the suite reflects reality, not aspiration.</para>
/// </summary>
internal static class Assemblies
{
    // Pure permission-evaluation primitives — explicitly meant to be safe
    // for external resource servers, must not pull in any IdP infrastructure.
    public static readonly Assembly PermissionsAbstractions =
        typeof(Cocoar.Auth.Permissions.PermissionEvaluator).Assembly;

    // Domain — events + value objects + aggregates.
    public static readonly Assembly Domain =
        typeof(Cocoar.Auth.Domain.OAuth.Applications.OAuthApplicationAggregate).Assembly;

    // Application — DTOs, OAuth admin service, policy abstractions.
    // (Marten + Wolverine are intentional in here because OAuthAdminService
    // does aggregate-stream operations directly. So Marten/Wolverine purity
    // is NOT enforced for Application — only ASP.NET / web-tier purity is.)
    public static readonly Assembly Application =
        typeof(Cocoar.Auth.Application.Services.OAuthAdminService).Assembly;

    // Api — slices, endpoints, hubs, Wolverine wiring.
    // Program.cs uses top-level statements (no public Program type), so
    // anchor on a public static endpoint class instead.
    public static readonly Assembly Api =
        typeof(Cocoar.Auth.Api.Features.Admin.AppSettingsEndpoints).Assembly;
}

internal static class TestResultFormatter
{
    /// <summary>
    /// Renders a NetArchTest result as a multi-line string for xUnit
    /// assertion messages. Avoids dragging in FluentAssertions.
    /// </summary>
    public static string Format(NetArchTest.Rules.TestResult result, string ruleDescription)
    {
        if (result.IsSuccessful)
            return string.Empty;

        var failing = result.FailingTypeNames ?? new List<string>();
        var lines = new List<string>
        {
            $"Architecture rule violated: {ruleDescription}",
            $"Failing types ({failing.Count}):",
        };
        lines.AddRange(failing.Select(t => "  - " + t));
        return string.Join(Environment.NewLine, lines);
    }
}
