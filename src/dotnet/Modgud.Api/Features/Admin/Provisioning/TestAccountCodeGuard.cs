using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using BuildingBlocks.Helper;
using Marten;
using Modgud.Authorization.Principals;

namespace Modgud.Api.Features.Admin.Provisioning;

/// <summary>
/// ADR 0026 — marking a test account and setting its fixed e-mail code is done by a person
/// in the admin console, never by a pipeline. The realm-config routes also accept a
/// Management API bearer token (realm:admin), so every route that takes a manifest refuses a
/// bearer request that carries a fixed code or would change a test-account marker. A marker
/// that matches the live state passes, so re-applying an export keeps working. A draft a
/// person staged may still be applied by a bearer: that person made the choice.
/// </summary>
internal static class TestAccountCodeGuard
{
    public static async Task<IResult?> RejectAsync(HttpContext http, IQuerySession session, RealmManifest? manifest, CancellationToken ct)
    {
        if (manifest is null || !IsBearer(http.Request)) return null;
        foreach (var u in manifest.Users)
        {
            if (!string.IsNullOrWhiteSpace(u.FixedEmailCode)) return Refused();
            if (u.IsTestAccount is { } wanted && await ChangesMarkerAsync(session, u.Id, u.Email, u.UserName, wanted, ct))
                return Refused();
        }
        return null;
    }

    public static async Task<IResult?> RejectAsync(HttpContext http, IQuerySession session, string section, JsonObject entity, CancellationToken ct)
    {
        if (!IsBearer(http.Request) || !string.Equals(section, "users", StringComparison.OrdinalIgnoreCase)) return null;
        if (Get(entity, "FixedEmailCode") is JsonValue code && code.TryGetValue<string>(out var c) && !string.IsNullOrWhiteSpace(c))
            return Refused();
        if (Get(entity, "IsTestAccount") is JsonValue marker && marker.TryGetValue<bool>(out var wanted)
            && await ChangesMarkerAsync(session, Str(entity, "Id"), Str(entity, "Email"), Str(entity, "UserName"), wanted, ct))
            return Refused();
        return null;
    }

    /// <summary>Whether <paramref name="wanted"/> differs from the user's live marker (a user
    /// that does not exist yet is not a test account).</summary>
    private static async Task<bool> ChangesMarkerAsync(
        IQuerySession session, string? id, string? email, string? userName, bool wanted, CancellationToken ct)
    {
        Person? person = null;
        if (!string.IsNullOrWhiteSpace(id) && ShortGuid.TryParse(id, out Guid guid))
            person = await session.LoadAsync<Person>(guid, ct);
        if (person is null && !string.IsNullOrWhiteSpace(email))
        {
            var normalized = email.ToUpperInvariant();
            person = await session.Query<Person>().FirstOrDefaultAsync(p => p.NormalizedEmail == normalized && !p.IsDeleted, ct);
        }
        if (person is null && !string.IsNullOrWhiteSpace(userName))
        {
            var normalized = userName.ToUpperInvariant();
            person = await session.Query<Person>().FirstOrDefaultAsync(p => p.NormalizedUserName == normalized && !p.IsDeleted, ct);
        }
        return (person?.IsTestAccount ?? false) != wanted;
    }

    private static JsonNode? Get(JsonObject entity, string field) =>
        entity.FirstOrDefault(p => string.Equals(p.Key, field, StringComparison.OrdinalIgnoreCase)).Value;

    private static string? Str(JsonObject entity, string field) =>
        Get(entity, field) is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static bool IsBearer(HttpRequest request) =>
        AuthenticationHeaderValue.TryParse(request.Headers.Authorization, out var value)
        && string.Equals(value.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase);

    private static IResult Refused() => Results.Problem(
        statusCode: StatusCodes.Status403Forbidden,
        title: "TestAccount.NeedsAPerson",
        detail: "Test accounts are marked and their fixed e-mail code is set by a person in the admin console, not through the Management API.");
}
