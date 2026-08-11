using System.Text.Json;

namespace Modgud.Api.Features.Admin;

/// <summary>
/// Server-owned publish boundary for tenant-authored PageBuilder documents.
/// Browser validation improves authoring UX; this validator is the authority
/// that decides which JSON may reach anonymous authentication surfaces.
/// </summary>
public static class PageDocumentValidator
{
    private const int MinimumSchemaVersion = 4;

    /// <summary>
    /// Page Builder 3.0 stamps saved documents as 6. The only document change is
    /// <c>repeat.props.source</c> → <c>repeat.props.contextPath</c>, which the
    /// package migrates on every ingest path — so a stored v4/v5 document still
    /// opens, and both spellings can arrive here (see the repeat check below).
    /// </summary>
    private const int CurrentSchemaVersion = 6;
    private const int MaxNodes = 500;
    private const int MaxDepth = 30;
    private const int MaxCodeLength = 64 * 1024;
    private const int MaxVisualHtmlLength = 100_000;
    private const int MaxVisualCssLength = 50_000;

    private static readonly HashSet<string> Slots =
        ["login", "password-forgot", "logout", "consent"];

    private static readonly HashSet<string> ElementTypes =
    [
        "page", "stack", "repeat", "card", "section", "divider", "spacer",
        "heading", "paragraph", "note", "feedback", "text-input",
        "password-input", "otp-input", "checkbox", "button", "link", "image",
        "visual-markup",
        "modgud-brand-header",
    ];

    private static readonly Dictionary<string, HashSet<string>> Actions = new(StringComparer.Ordinal)
    {
        ["login"] =
        [
            "auth:login", "auth:passkey", "auth:magic-link",
            "auth:request-login-code", "auth:verify-login-code",
            "auth:resend-login-code", "auth:back-to-email", "auth:toggle-language",
            "auth:forgot-password", "auth:register", "auth:external-provider",
            "legal:terms", "legal:privacy",
        ],
        ["password-forgot"] =
        [
            "auth:send-reset-link", "auth:back-to-login",
            "legal:terms", "legal:privacy",
        ],
        ["logout"] = ["auth:back-to-login", "legal:terms", "legal:privacy"],
        ["consent"] =
        [
            "auth:consent-deny", "auth:consent-allow",
            "legal:terms", "legal:privacy",
        ],
    };

    public static bool Validate(string slug, string schema, out string error)
    {
        if (!Slots.Contains(slug))
        {
            error = $"Unsupported page slot '{slug}'.";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(schema, new JsonDocumentOptions
            {
                MaxDepth = MaxDepth + 8,
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false,
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !StringProperty(root, "type", out var rootType)
                || rootType != "page")
            {
                error = "Root node must be an object with type 'page'.";
                return false;
            }
            if (!root.TryGetProperty("schemaVersion", out var version)
                || !version.TryGetInt32(out var schemaVersion)
                || schemaVersion is < MinimumSchemaVersion or > CurrentSchemaVersion)
            {
                error = $"Page schemaVersion must be between {MinimumSchemaVersion} and {CurrentSchemaVersion}.";
                return false;
            }

            var ids = new HashSet<string>(StringComparer.Ordinal);
            var nodeCount = 0;
            JsonElement? consentCard = null;
            JsonElement? warning = null;
            var warningIndex = -1;
            var validationError = string.Empty;

            bool Walk(JsonElement node, int depth, string? parentId, int index)
            {
                if (depth > MaxDepth) { validationError = $"Document exceeds maximum depth {MaxDepth}."; return false; }
                if (++nodeCount > MaxNodes) { validationError = $"Document exceeds maximum node count {MaxNodes}."; return false; }
                if (node.ValueKind != JsonValueKind.Object) { validationError = "Every node must be an object."; return false; }
                if (!StringProperty(node, "id", out var id) || string.IsNullOrWhiteSpace(id))
                { validationError = "Every node needs a non-empty id."; return false; }
                if (!ids.Add(id)) { validationError = $"Duplicate node id '{id}'."; return false; }
                if (!StringProperty(node, "type", out var type) || !ElementTypes.Contains(type))
                { validationError = $"Node '{id}' uses a disallowed type."; return false; }
                if (depth > 0 && type == "page") { validationError = "Only the root may have type 'page'."; return false; }

                if (node.TryGetProperty("name", out var nameElement)
                    && nameElement.ValueKind == JsonValueKind.String
                    && nameElement.GetString() is "__proto__" or "prototype" or "constructor")
                { validationError = $"Node '{id}' uses an unsafe field name."; return false; }

                if (node.TryGetProperty("elementCode", out var elementCode)
                    && (elementCode.ValueKind != JsonValueKind.String
                        || elementCode.GetString()!.Length > MaxCodeLength))
                { validationError = $"Node '{id}' has invalid or oversized Element Code."; return false; }
                if (depth == 0 && node.TryGetProperty("stateCode", out var stateCode)
                    && (stateCode.ValueKind != JsonValueKind.String
                        || stateCode.GetString()!.Length > MaxCodeLength))
                { validationError = "Page State code is invalid or oversized."; return false; }

                if (node.TryGetProperty("props", out var props))
                {
                    if (props.ValueKind != JsonValueKind.Object)
                    { validationError = $"Node '{id}' props must be an object."; return false; }
                    if (StringProperty(props, "action", out var action)
                        && !Actions[slug].Contains(action))
                    { validationError = $"Node '{id}' references disallowed action '{action}'."; return false; }
                    if (type == "repeat")
                    {
                        // schemaVersion 6 renamed this to contextPath. Accept the new
                        // spelling first and fall back to the old one, mirroring the
                        // package's own migration: it skips when the new key is already
                        // present. Documents authored before the rename are still
                        // submittable because MinimumSchemaVersion is unchanged.
                        if (!StringProperty(props, "contextPath", out var source)
                            && !StringProperty(props, "source", out source))
                        { validationError = $"Node '{id}' references a disallowed repeat source."; return false; }
                        if ((slug == "login" && source != "auth.externalProviders")
                            || (slug == "consent" && source != "consent.requestedScopes")
                            || (slug is not "login" and not "consent"))
                        { validationError = $"Node '{id}' references a disallowed repeat source."; return false; }
                        if (props.TryGetProperty("maxItems", out var maxItems)
                            && (!maxItems.TryGetInt32(out var max) || max is < 1 or > 100))
                        { validationError = $"Node '{id}' repeat maxItems must be between 1 and 100."; return false; }
                    }
                    if (type == "visual-markup")
                    {
                        if (!StringProperty(props, "html", out var html)
                            || html.Length > MaxVisualHtmlLength)
                        { validationError = $"Node '{id}' visual HTML is missing or exceeds {MaxVisualHtmlLength} characters."; return false; }
                        if (props.TryGetProperty("css", out var css)
                            && (css.ValueKind != JsonValueKind.String
                                || css.GetString()!.Length > MaxVisualCssLength))
                        { validationError = $"Node '{id}' visual CSS exceeds {MaxVisualCssLength} characters."; return false; }
                    }
                }

                if (id == "consent-card") consentCard = node;
                if (id == "unverified-client-warning")
                {
                    warning = node;
                    if (parentId == "consent-card") warningIndex = index;
                }

                if (!node.TryGetProperty("children", out var children)) return true;
                if (children.ValueKind != JsonValueKind.Array)
                { validationError = $"Node '{id}' children must be an array."; return false; }
                var childIndex = 0;
                foreach (var child in children.EnumerateArray())
                {
                    if (!Walk(child, depth + 1, id, childIndex++)) return false;
                }
                return true;
            }

            if (!Walk(root, 0, null, 0))
            {
                error = validationError;
                return false;
            }
            if (slug == "consent")
            {
                if (consentCard is null || warning is null || warningIndex is < 0 or > 4)
                {
                    error = "Consent requires the locked unverified-client warning near the top of consent-card.";
                    return false;
                }
                if (!StringProperty(warning.Value, "type", out var warningType) || warningType != "note")
                {
                    error = "Consent unverified-client-warning must be a note.";
                    return false;
                }
            }

            error = string.Empty;
            return true;
        }
        catch (JsonException ex)
        {
            error = $"Schema is not valid JSON: {ex.Message}";
            return false;
        }
    }

    private static bool StringProperty(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
            return false;
        value = property.GetString() ?? string.Empty;
        return true;
    }
}
