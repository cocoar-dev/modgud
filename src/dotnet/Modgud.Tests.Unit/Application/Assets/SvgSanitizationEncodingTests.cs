using System.Text;
using System.Xml;
using Modgud.Application.Assets;

namespace Modgud.Tests.Unit.Application.Assets;

/// <summary>
/// B10-PA01 — the sanitized SVG was saved through a StringWriter (UTF-16), so its XML
/// declaration said encoding="utf-16", while the stored bytes were UTF-8. An XML-conforming
/// reader then decodes the asset wrongly. Declaration and bytes now agree on UTF-8.
/// </summary>
public sealed class SvgSanitizationEncodingTests
{
    [Fact]
    public void The_sanitized_svg_declares_the_encoding_of_its_bytes()
    {
        var raw = Encoding.UTF8.GetBytes("""<?xml version="1.0" encoding="utf-8"?><svg xmlns="http://www.w3.org/2000/svg"><title>Grüße</title></svg>""");

        var result = AssetValidation.SanitizeSvg(raw);

        Assert.False(result.IsError);
        var text = Encoding.UTF8.GetString(result.Value);
        Assert.DoesNotContain("utf-16", text, StringComparison.OrdinalIgnoreCase);

        // A conforming reader honours the declaration; the title must survive the round trip.
        using var reader = XmlReader.Create(new MemoryStream(result.Value));
        var doc = System.Xml.Linq.XDocument.Load(reader);
        Assert.Equal("Grüße", doc.Root!.Elements().Single().Value);
    }
}
