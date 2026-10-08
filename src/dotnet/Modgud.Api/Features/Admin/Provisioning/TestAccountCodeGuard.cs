using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace Modgud.Api.Features.Admin.Provisioning;

/// <summary>
/// ADR 0026 — a test account's fixed e-mail code is set by a person in the admin console,
/// never by a pipeline. The realm-config routes also accept a Management API bearer token
/// (realm:admin), so every route that takes a manifest refuses one carrying a code when the
/// request is bearer-authenticated. A draft a person staged may still be applied by a bearer:
/// the code was chosen by that person.
/// </summary>
internal static class TestAccountCodeGuard
{
    private const string Field = "FixedEmailCode";

    public static IResult? Reject(HttpContext http, RealmManifest? manifest) =>
        IsBearer(http.Request) && manifest?.Users.Any(u => !string.IsNullOrWhiteSpace(u.FixedEmailCode)) == true
            ? Refused()
            : null;

    public static IResult? Reject(HttpContext http, string section, JsonObject entity) =>
        IsBearer(http.Request)
        && string.Equals(section, "users", StringComparison.OrdinalIgnoreCase)
        && entity.Any(p => string.Equals(p.Key, Field, StringComparison.OrdinalIgnoreCase)
                           && p.Value is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
            ? Refused()
            : null;

    private static bool IsBearer(HttpRequest request) =>
        AuthenticationHeaderValue.TryParse(request.Headers.Authorization, out var value)
        && string.Equals(value.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase);

    private static IResult Refused() => Results.Problem(
        statusCode: StatusCodes.Status403Forbidden,
        title: "TestAccount.CodeNeedsAPerson",
        detail: "A test account's fixed e-mail code is set by a person in the admin console, not through the Management API.");
}
