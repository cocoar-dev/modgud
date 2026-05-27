using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Modgud.Authentication.Identity.LoginProviders.Saml;

/// <summary>
/// Strongly-typed view over the JSON blob stored in <c>LoginProvider.FlavorData</c>
/// when <c>LoginProvider.Type == Saml</c>. This is the *IdP-side* configuration
/// the admin provides plus the denormalised IdP certificates we cache from
/// metadata fetches; the SP-side (our own signing/encryption cert per realm)
/// lives separately and is never part of <c>FlavorData</c>.
/// <para>
/// Shape mirrors the proposal in <c>dev-docs/future-features/saml-federation.md</c>.
/// Unknown fields are tolerated on parse so we can extend the schema forward-
/// compatibly without breaking older stored docs.
/// </para>
/// </summary>
public sealed record SamlFlavorData
{
    /// <summary>
    /// IdP federation-metadata URL. Either this or <see cref="MetadataXml"/>
    /// must be set. Metadata-URL is preferred because it enables automatic
    /// cert-rotation discovery via the <see cref="MetadataRefreshIntervalSeconds"/>
    /// refresh job.
    /// </summary>
    public string? MetadataUrl { get; init; }

    /// <summary>
    /// Pasted IdP metadata XML. Used when the customer's IdP doesn't publish a
    /// reachable metadata URL (e.g. on-prem ADFS behind a firewall). Without a
    /// URL the cert-rotation story falls to manual XML re-paste.
    /// </summary>
    public string? MetadataXml { get; init; }

    /// <summary>
    /// SAML IdP Entity ID. Optional; if absent, parsed from the IdP metadata
    /// document on first refresh.
    /// </summary>
    public string? EntityId { get; init; }

    /// <summary>
    /// IdP signing certificates (base64-encoded X.509 DER), denormalised from
    /// the most recent metadata fetch. Multiple entries support the IdP-side
    /// key-rollover pattern (overlap window with current + next signing key).
    /// Populated by the metadata refresh job, not directly editable by admins.
    /// </summary>
    public IReadOnlyList<string> SigningCertificates { get; init; } = [];

    /// <summary>
    /// Requested NameID format URI in outgoing <c>AuthnRequest</c>. Default is
    /// the SAML 2.0 emailAddress format. See <see cref="SamlNameIdFormats"/>
    /// for the well-known values.
    /// </summary>
    public string NameIdFormat { get; init; } = SamlNameIdFormats.EmailAddress;

    /// <summary>If true, refuse responses whose assertions are unsigned.</summary>
    public bool WantAssertionsSigned { get; init; } = true;

    /// <summary>If true, refuse responses whose <c>Response</c> wrapper is unsigned.</summary>
    public bool WantResponseSigned { get; init; } = true;

    /// <summary>
    /// If true, require the assertion to be XML-encrypted (in addition to being
    /// signed). Default off — assertion encryption is rare in practice; most
    /// IdPs only encrypt over TLS.
    /// </summary>
    public bool WantAssertionsEncrypted { get; init; }

    /// <summary>If true, sign outgoing <c>AuthnRequest</c> with the realm's SP signing key.</summary>
    public bool SignAuthnRequest { get; init; } = true;

    /// <summary>
    /// Map of logical claim names (<c>email</c>, <c>name</c>, <c>groups</c>, …)
    /// to one-or-more SAML attribute URIs the IdP emits for that claim. Multiple
    /// URIs per logical name absorb cross-IdP naming differences (e.g.
    /// Microsoft's <c>http://schemas.microsoft.com/...</c> vs the SAML 2.0 standard
    /// short names).
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> AttributeMap { get; init; }
        = FrozenDictionary<string, IReadOnlyList<string>>.Empty;

    /// <summary>
    /// Map of SAML <c>AuthnContextClassRef</c> URIs to AMR (Authentication Method
    /// Reference) values to stamp onto the Modgud session principal. Mirrors the
    /// OIDC <c>amr</c>-claim preservation pattern — values flow into Modgud's
    /// federated-MFA detection.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> AmrMapping { get; init; }
        = FrozenDictionary<string, IReadOnlyList<string>>.Empty;

    /// <summary>
    /// Metadata-refresh cadence in seconds. Default 24h. Allowed range is
    /// enforced at the API boundary (1h / 6h / 24h / 7d per
    /// <c>dev-docs/future-features/saml-federation.md</c> — open-ended seconds
    /// here keeps the schema flexible for tests / future tuning).
    /// </summary>
    public int MetadataRefreshIntervalSeconds { get; init; } = DefaultMetadataRefreshIntervalSeconds;

    /// <summary>Default metadata refresh cadence — 24 hours.</summary>
    public const int DefaultMetadataRefreshIntervalSeconds = 86_400;

    /// <summary>
    /// Parse a <see cref="JsonDocument"/> (as stored on
    /// <c>LoginProvider.FlavorData</c>) into a typed <see cref="SamlFlavorData"/>.
    /// Returns a record with defaults when <paramref name="flavorData"/> is null
    /// or when individual fields are missing — never throws on shape variance.
    /// Throws only on outright JSON corruption (caller is expected to surface
    /// that as a 400 / domain error).
    /// </summary>
    public static SamlFlavorData FromJson(JsonDocument? flavorData)
    {
        if (flavorData is null)
            return new SamlFlavorData();

        var root = flavorData.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            return new SamlFlavorData();

        return new SamlFlavorData
        {
            MetadataUrl = TryGetString(root, "metadataUrl"),
            MetadataXml = TryGetString(root, "metadataXml"),
            EntityId = TryGetString(root, "entityId"),
            SigningCertificates = TryGetStringArray(root, "signingCertificates"),
            NameIdFormat = TryGetString(root, "nameIdFormat") ?? SamlNameIdFormats.EmailAddress,
            WantAssertionsSigned = TryGetBool(root, "wantAssertionsSigned") ?? true,
            WantResponseSigned = TryGetBool(root, "wantResponseSigned") ?? true,
            WantAssertionsEncrypted = TryGetBool(root, "wantAssertionsEncrypted") ?? false,
            SignAuthnRequest = TryGetBool(root, "signAuthnRequest") ?? true,
            AttributeMap = TryGetStringArrayMap(root, "attributeMap"),
            AmrMapping = TryGetStringArrayMap(root, "amrMapping"),
            MetadataRefreshIntervalSeconds = TryGetInt32(root, "metadataRefreshIntervalSeconds")
                ?? DefaultMetadataRefreshIntervalSeconds,
        };
    }

    /// <summary>
    /// Serialise back to a <see cref="JsonDocument"/>. Field naming is
    /// camelCase to match the on-disk storage convention; null / empty
    /// collections are written explicitly so consumers see a stable shape.
    /// </summary>
    public JsonDocument ToJson()
    {
        var node = new JsonObject
        {
            ["metadataUrl"] = MetadataUrl,
            ["metadataXml"] = MetadataXml,
            ["entityId"] = EntityId,
            ["signingCertificates"] = new JsonArray(SigningCertificates.Select(c => (JsonNode?)c).ToArray()),
            ["nameIdFormat"] = NameIdFormat,
            ["wantAssertionsSigned"] = WantAssertionsSigned,
            ["wantResponseSigned"] = WantResponseSigned,
            ["wantAssertionsEncrypted"] = WantAssertionsEncrypted,
            ["signAuthnRequest"] = SignAuthnRequest,
            ["attributeMap"] = SerialiseStringArrayMap(AttributeMap),
            ["amrMapping"] = SerialiseStringArrayMap(AmrMapping),
            ["metadataRefreshIntervalSeconds"] = MetadataRefreshIntervalSeconds,
        };
        return JsonDocument.Parse(node.ToJsonString());
    }

    // FlavorConfigField keys are PascalCase ("MetadataUrl") to match the OIDC
    // convention surfaced in the admin UI, but the FromJson canonical form is
    // camelCase ("metadataUrl"). The frontend serialises the admin form using
    // the field Keys verbatim. Plus once an update goes through and we
    // re-persist via ToJson (camelCase), the document ends up carrying BOTH
    // forms — camelCase as the canonical (possibly null) and PascalCase as
    // the admin-set form (with the real value). Resolve both forms and
    // prefer whichever has a non-null value.
    private static bool TryGetPropertyCaseInsensitive(JsonElement root, string camelName, out JsonElement value)
    {
        var pascal = camelName.Length > 0
            ? char.ToUpperInvariant(camelName[0]) + camelName[1..]
            : camelName;

        var hasCamel = root.TryGetProperty(camelName, out var camelEl);
        var hasPascal = !string.Equals(camelName, pascal, StringComparison.Ordinal)
            && root.TryGetProperty(pascal, out var pascalEl);

        // If both exist, prefer the one that is not null/undefined.
        if (hasCamel && hasPascal)
        {
            var camelIsValue = camelEl.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined;
            if (camelIsValue)
            {
                value = camelEl;
                return true;
            }
            value = root.GetProperty(pascal);
            return true;
        }

        if (hasCamel)
        {
            value = camelEl;
            return true;
        }

        if (hasPascal)
        {
            value = root.GetProperty(pascal);
            return true;
        }

        value = default;
        return false;
    }

    private static string? TryGetString(JsonElement root, string name) =>
        TryGetPropertyCaseInsensitive(root, name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    private static bool? TryGetBool(JsonElement root, string name) =>
        TryGetPropertyCaseInsensitive(root, name, out var el) && (el.ValueKind == JsonValueKind.True || el.ValueKind == JsonValueKind.False)
            ? el.GetBoolean()
            : null;

    private static int? TryGetInt32(JsonElement root, string name) =>
        TryGetPropertyCaseInsensitive(root, name, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var n)
            ? n
            : null;

    private static IReadOnlyList<string> TryGetStringArray(JsonElement root, string name)
    {
        if (!TryGetPropertyCaseInsensitive(root, name, out var el) || el.ValueKind != JsonValueKind.Array)
            return [];
        return el.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => x.GetString()!)
            .ToArray();
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> TryGetStringArrayMap(JsonElement root, string name)
    {
        if (!TryGetPropertyCaseInsensitive(root, name, out var el) || el.ValueKind != JsonValueKind.Object)
            return FrozenDictionary<string, IReadOnlyList<string>>.Empty;

        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var prop in el.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.Array)
            {
                result[prop.Name] = prop.Value.EnumerateArray()
                    .Where(x => x.ValueKind == JsonValueKind.String)
                    .Select(x => x.GetString()!)
                    .ToArray();
            }
            else if (prop.Value.ValueKind == JsonValueKind.String)
            {
                result[prop.Name] = [prop.Value.GetString()!];
            }
        }
        return result.ToFrozenDictionary();
    }

    private static JsonObject SerialiseStringArrayMap(IReadOnlyDictionary<string, IReadOnlyList<string>> map)
    {
        var obj = new JsonObject();
        foreach (var (k, values) in map)
        {
            obj[k] = new JsonArray(values.Select(v => (JsonNode?)v).ToArray());
        }
        return obj;
    }
}

/// <summary>
/// Well-known SAML 2.0 NameID format URIs. The constants are the on-wire string
/// values — store them verbatim on <see cref="SamlFlavorData.NameIdFormat"/> so
/// IdP <c>AuthnRequest</c>s emit the exact URI the spec specifies.
/// </summary>
public static class SamlNameIdFormats
{
    public const string EmailAddress = "urn:oasis:names:tc:SAML:1.1:nameid-format:emailAddress";
    public const string Persistent = "urn:oasis:names:tc:SAML:2.0:nameid-format:persistent";
    public const string Transient = "urn:oasis:names:tc:SAML:2.0:nameid-format:transient";
    public const string Unspecified = "urn:oasis:names:tc:SAML:1.1:nameid-format:unspecified";
}
