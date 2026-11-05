using Modgud.Infrastructure.Email;

namespace Modgud.Tests.Unit.Infrastructure.Email;

/// <summary>
/// B3-EI-03 — the plain-text alternative stripped tags together with their href, so a
/// text-only reader got "Verify email" without the link that magic-link, reset, verification
/// and bootstrap mails depend on. A link keeps its URL in the text.
/// </summary>
public sealed class PlainTextActionLinkTests
{
    [Fact]
    public void An_action_link_keeps_its_url_in_the_plain_text()
    {
        var text = EmailTemplateStore.ToPlainText(
            """<p>Hello</p><p><a href="https://idp.test/verify?token=abc&amp;t=1" class="btn">Verify email</a></p>""");

        Assert.Contains("Verify email", text);
        Assert.Contains("https://idp.test/verify?token=abc&t=1", text);
    }

    [Fact]
    public void A_link_whose_text_is_its_url_is_written_once()
    {
        var text = EmailTemplateStore.ToPlainText("""<a href="https://idp.test/x">https://idp.test/x</a>""");

        Assert.Equal("https://idp.test/x", text);
    }
}
