using System.Text.Json;
using Marten;
using Modgud.Domain.OAuth.Scopes;

namespace Modgud.Application.Dcr;

/// <summary>
/// Which scopes a <em>dynamic</em> client — one minted via Dynamic Client
/// Registration or synthesized from a CIMD document — may hold and request.
/// The one rule behind three seams: the CIMD resolver (permissions on the
/// synthesized client), the DCR registration (permissions on the stored
/// client) and the authorize-time check.
///
/// <para>A scope is requestable by dynamic clients when it is enabled and
/// either carries <see cref="ScopePropertyKeys.AllowDynamicRegistrationClients"/>
/// — the admin's per-scope opt-in, the flag's whole meaning — or is one of the
/// standard scopes other than <c>modgud.management</c>. The standard scopes
/// cannot carry the flag (they are immutable), so they need a built-in
/// verdict: the identity and claim-gate scopes name no resource privilege of
/// their own, an unverified client may ask for them; the management selector
/// names the IdP's own management API and is exactly the "tenant.admin.*"
/// case the flag exists to keep away from a habit-click consent (the realm
/// seeder strips the flag from it for the same reason).</para>
///
/// <para>The client's own <c>scope</c> declaration is an upper bound, never
/// a grant: declared scopes are intersected with this set, and a client that
/// declares none gets the whole set (RFC 7591 §2 — "a default set of
/// scopes"). A static CIMD document published for every MCP server in the
/// world cannot know one server's scopes; the set makes such a client work
/// without letting it invent permissions.</para>
/// </summary>
public static class DynamicClientScopePolicy
{
    /// <summary>The scope may be held and requested by dynamic clients. A
    /// standard scope is judged by the built-in verdict alone — a flag that
    /// found its way onto one (the seeder strips it) changes nothing.</summary>
    public static bool IsRequestable(OAuthScopeState scope)
        => scope is { IsDeleted: false, Enabled: true }
           && (StandardScopes.IsStandard(scope.Name) ? IsOpenStandardScope(scope.Name) : HasOptIn(scope.Properties));

    /// <summary>A standard scope that dynamic clients may always request —
    /// every standard scope except the management-API selector.</summary>
    public static bool IsOpenStandardScope(string? name)
        => StandardScopes.IsStandard(name) && name != StandardScopes.Management;

    /// <summary>Reads the per-scope opt-in flag (plain bool or JsonElement,
    /// depending on which serializer wrote the document).</summary>
    public static bool HasOptIn(IDictionary<string, object?> properties)
    {
        if (!properties.TryGetValue(ScopePropertyKeys.AllowDynamicRegistrationClients, out var raw) || raw is null)
            return false;
        return raw switch
        {
            bool b => b,
            JsonElement e when e.ValueKind is JsonValueKind.True => true,
            _ => false,
        };
    }

    /// <summary>
    /// The scope names a dynamic client ends up holding: the declared names
    /// intersected with the requestable set, or the whole requestable set when
    /// nothing was declared. Order is preserved from <paramref name="requestable"/>
    /// so the result is stable across resolves.
    /// </summary>
    public static List<string> Resolve(IReadOnlyCollection<string> declared, IReadOnlyList<string> requestable)
    {
        if (declared.Count == 0) return requestable.ToList();
        var wanted = declared.ToHashSet(StringComparer.Ordinal);
        return requestable.Where(wanted.Contains).ToList();
    }

    /// <summary>
    /// Loads the requestable scope names of the current realm: standard scopes
    /// first in their canonical order, then the opted-in ones by name.
    /// </summary>
    public static async Task<List<string>> LoadRequestableNamesAsync(IQuerySession session, CancellationToken ct)
    {
        var scopes = await session.Query<OAuthScopeState>()
            .Where(s => !s.IsDeleted && s.Enabled)
            .ToListAsync(ct);
        var requestable = scopes.Where(IsRequestable).Select(s => s.Name).ToHashSet(StringComparer.Ordinal);

        var ordered = StandardScopes.All.Where(requestable.Contains).ToList();
        ordered.AddRange(requestable.Except(StandardScopes.All, StringComparer.Ordinal).Order(StringComparer.Ordinal));
        return ordered;
    }
}
