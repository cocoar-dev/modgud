using System.Xml;
using System.Xml.Linq;
using ErrorOr;

namespace Modgud.Application.Assets;

/// <summary>
/// Server-side validation for asset uploads — magic-byte sniffing for the
/// content-type (NEVER trust the client's <c>Content-Type</c> header), size
/// cap, MIME allowlist, and SVG sanitization. Defense-in-depth on top of
/// the existing CSP <c>img-src 'self' data:</c>.
///
/// <para>Single static class on purpose — pure-function validators with no
/// state, no DI dependency, trivially unit-testable.</para>
/// </summary>
public static class AssetValidation
{
    public const long MaxSizeBytes = 2 * 1024 * 1024; // 2 MiB

    /// <summary>Allowlisted image MIME types. Any other detected type rejects.</summary>
    public static readonly IReadOnlySet<string> AllowedMimeTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "image/png",
        "image/jpeg",
        "image/gif",
        "image/webp",
        "image/svg+xml",
        "image/x-icon",
        "image/vnd.microsoft.icon",
    };

    /// <summary>
    /// Sniffs the content type from the leading magic bytes. Returns
    /// <c>null</c> if no allowlisted type matches — caller rejects.
    /// SVG-detection accepts whitespace + optional XML declaration before
    /// the <c>&lt;svg&gt;</c> root.
    /// </summary>
    public static string? SniffContentType(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 4) return null;

        // PNG: 89 50 4E 47 0D 0A 1A 0A
        if (bytes.Length >= 8
            && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47
            && bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A)
            return "image/png";

        // JPEG: FF D8 FF
        if (bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            return "image/jpeg";

        // GIF: 47 49 46 38 (37|39) 61
        if (bytes.Length >= 6
            && bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x38
            && (bytes[4] == 0x37 || bytes[4] == 0x39) && bytes[5] == 0x61)
            return "image/gif";

        // WebP: "RIFF" + 4 bytes + "WEBP"
        if (bytes.Length >= 12
            && bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46
            && bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50)
            return "image/webp";

        // ICO: 00 00 01 00
        if (bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0x01 && bytes[3] == 0x00)
            return "image/x-icon";

        // SVG — text-based. Allow optional UTF-8 BOM, whitespace, XML decl,
        // DOCTYPE, then "<svg". Skip up to first "<svg" within first 1 KiB.
        var head = bytes[..Math.Min(bytes.Length, 1024)];
        if (LooksLikeSvg(head)) return "image/svg+xml";

        return null;
    }

    private static bool LooksLikeSvg(ReadOnlySpan<byte> head)
    {
        // Skip BOM.
        var i = 0;
        if (head.Length >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF) i = 3;

        // Find first '<svg' (case-insensitive).
        ReadOnlySpan<byte> needle = "<svg"u8;
        for (; i <= head.Length - needle.Length; i++)
        {
            // Quick byte-compare with case-fold on letters.
            if ((head[i] == 0x3C)
                && (head[i + 1] == 0x73 || head[i + 1] == 0x53)
                && (head[i + 2] == 0x76 || head[i + 2] == 0x56)
                && (head[i + 3] == 0x67 || head[i + 3] == 0x47))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Strips script content + JavaScript URI handlers from an SVG payload.
    /// Removes <c>&lt;script&gt;</c>, <c>&lt;foreignObject&gt;</c>, every
    /// <c>on*</c>-attribute (event handlers), and any
    /// <c>href</c>/<c>xlink:href</c> whose value starts with
    /// <c>javascript:</c> or <c>data:text/html</c>. Reserializes the
    /// resulting tree.
    ///
    /// <para>Returns an error if the input isn't well-formed XML — we
    /// refuse to persist anything we can't fully parse, on the principle
    /// that a parser disagreement between us and the browser is a
    /// sanitization bypass waiting to happen.</para>
    /// </summary>
    public static ErrorOr<byte[]> SanitizeSvg(ReadOnlySpan<byte> raw)
    {
        XDocument doc;
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,
                XmlResolver = null,
            };
            using var ms = new MemoryStream(raw.ToArray());
            using var reader = XmlReader.Create(ms, settings);
            doc = XDocument.Load(reader);
        }
        catch (Exception ex)
        {
            return Error.Validation(
                "Asset.SvgNotWellFormed",
                $"SVG could not be parsed: {ex.Message}");
        }

        StripDangerousNodes(doc);

        var sw = new StringWriter();
        doc.Save(sw, SaveOptions.DisableFormatting);
        return System.Text.Encoding.UTF8.GetBytes(sw.ToString());
    }

    private static void StripDangerousNodes(XDocument doc)
    {
        // <script> + <foreignObject> get removed entirely.
        var toRemove = doc.Descendants()
            .Where(e => string.Equals(e.Name.LocalName, "script", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(e.Name.LocalName, "foreignObject", StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var n in toRemove) n.Remove();

        // Attributes: every on*= (event handler) + every href/xlink:href
        // that points at a javascript: or data:text/html URI.
        foreach (var el in doc.Descendants().ToList())
        {
            var dangerousAttrs = el.Attributes()
                .Where(a => a.Name.LocalName.StartsWith("on", StringComparison.OrdinalIgnoreCase)
                         || (IsHrefAttribute(a) && IsDangerousUri(a.Value)))
                .ToList();
            foreach (var a in dangerousAttrs) a.Remove();
        }
    }

    private static bool IsHrefAttribute(XAttribute a) =>
        string.Equals(a.Name.LocalName, "href", StringComparison.OrdinalIgnoreCase);

    private static bool IsDangerousUri(string value)
    {
        var trimmed = value.TrimStart().ToLowerInvariant();
        return trimmed.StartsWith("javascript:", StringComparison.Ordinal)
            || trimmed.StartsWith("data:text/html", StringComparison.Ordinal)
            || trimmed.StartsWith("vbscript:", StringComparison.Ordinal);
    }
}
