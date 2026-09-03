using System.Net;
using System.Net.Http.Json;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Application.DTOs.RealmSettings;

namespace Modgud.Api.Tests.Authorization;

/// <summary>
/// The admin email preview renders the REAL template through the real store with
/// the effective branding — so what the admin sees is what a user receives.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class EmailPreviewTests(SharedPostgresFixture fixture) : IntegrationTestBase(fixture)
{
    private const string Endpoint = "/api/admin/realm-settings/email-preview";

    private sealed record Preview(string Template, string Subject, string From, string? ReplyTo, string HtmlBody, string TextBody);

    [Fact]
    public async Task Renders_the_otp_template_with_the_stored_branding_and_sender()
    {
        var ct = TestContext.Current.CancellationToken;
        var patch = await Client.PatchAsJsonAsync("/api/admin/realm-settings", new UpdateRealmSettingsDto
        {
            EmailBranding = new UpdateEmailBrandingSettingsDto
            {
                ProductName = "Acme Mail",
                FromName = "Acme Security",
                FromAddress = "security@acme.test",
                ReplyTo = "help@acme.test",
                FooterText = "Acme footer line",
            },
        }, JsonOptions, ct);
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

        var response = await Client.PostAsJsonAsync(Endpoint, new { Template = "EmailOtp", Language = "en" }, JsonOptions, ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var preview = await response.Content.ReadFromJsonAsync<Preview>(JsonOptions, ct);
        Assert.NotNull(preview);

        Assert.Equal("EmailOtp", preview!.Template);
        Assert.Equal("Acme Security <security@acme.test>", preview.From);
        Assert.Equal("help@acme.test", preview.ReplyTo);
        Assert.Contains("Acme Mail", preview.Subject);
        // The sample code and the footer are in the rendered body — it IS the template.
        Assert.Contains("483 921", preview.HtmlBody);
        Assert.Contains("Acme footer line", preview.HtmlBody);
        Assert.Contains("483 921", preview.TextBody);
    }

    [Fact]
    public async Task Unsaved_form_values_overlay_the_stored_branding()
    {
        var ct = TestContext.Current.CancellationToken;
        // Nothing saved for this — the overlay alone must drive the preview, and an
        // empty string must CLEAR (fall back), exactly like the form's save would.
        var response = await Client.PostAsJsonAsync(Endpoint, new
        {
            Template = "MagicLink",
            Language = "de",
            ProductName = "Overlay Product",
            Branding = new { FromName = "Overlay Sender", FromAddress = "hello@overlay.test", FooterText = "" },
        }, JsonOptions, ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var preview = await response.Content.ReadFromJsonAsync<Preview>(JsonOptions, ct);

        Assert.Equal("Overlay Sender <hello@overlay.test>", preview!.From);
        Assert.Contains("Overlay Product", preview.HtmlBody);
    }

    [Fact]
    public async Task An_empty_product_name_in_the_form_falls_back_instead_of_leaving_the_placeholder()
    {
        var ct = TestContext.Current.CancellationToken;
        // The form sends "" for an untouched product name. A real send always has an
        // AppName (resolver fallback), so the preview must show the fallback — never a
        // raw "{{AppName}}" in the subject.
        var response = await Client.PostAsJsonAsync(Endpoint, new
        {
            Template = "EmailOtp",
            Language = "de",
            ProductName = "",
            Branding = new { ProductName = "", FromName = "", FromAddress = "" },
        }, JsonOptions, ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var preview = await response.Content.ReadFromJsonAsync<Preview>(JsonOptions, ct);

        Assert.DoesNotContain("{{", preview!.Subject);
        Assert.DoesNotContain("{{", preview.HtmlBody);
        Assert.EndsWith("Anmelde-Code", preview.Subject);
    }

    [Fact]
    public async Task Rejects_an_unknown_template()
    {
        var ct = TestContext.Current.CancellationToken;
        var response = await Client.PostAsJsonAsync(Endpoint, new { Template = "NotATemplate" }, JsonOptions, ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
