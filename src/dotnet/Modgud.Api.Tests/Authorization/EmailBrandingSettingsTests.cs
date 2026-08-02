using System.Net;
using System.Net.Http.Json;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Application.DTOs.RealmSettings;

namespace Modgud.Api.Tests.Authorization;

[Collection(IntegrationTestCollection.Name)]
public class EmailBrandingSettingsTests(SharedPostgresFixture fixture) : IntegrationTestBase(fixture)
{
    private const string Endpoint = "/api/admin/realm-settings";

    [Fact]
    public async Task Realm_email_branding_roundtrips_and_supports_explicit_clear()
    {
        var ct = TestContext.Current.CancellationToken;
        var response = await Client.PatchAsJsonAsync(Endpoint, new UpdateRealmSettingsDto
        {
            EmailBranding = new UpdateEmailBrandingSettingsDto
            {
                ProductName = "Realm Mail",
                SubjectPrefix = "Realm",
                Preheader = "Continue securely",
                FooterText = "Security team",
                FromName = "Realm Security",
                ReplyTo = "support@example.test",
            },
        }, JsonOptions, ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var configured = await Client.GetFromJsonAsync<RealmSettingsDto>(Endpoint, JsonOptions, ct);
        Assert.Equal("Realm Mail", configured!.EmailBranding.ProductName);
        Assert.Equal("Realm", configured.EmailBranding.SubjectPrefix);
        Assert.Equal("Continue securely", configured.EmailBranding.Preheader);
        Assert.Equal("Security team", configured.EmailBranding.FooterText);
        Assert.Equal("Realm Security", configured.EmailBranding.FromName);
        Assert.Equal("support@example.test", configured.EmailBranding.ReplyTo);

        response = await Client.PatchAsJsonAsync(Endpoint, new UpdateRealmSettingsDto
        {
            EmailBranding = new UpdateEmailBrandingSettingsDto { FooterText = "" },
        }, JsonOptions, ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var cleared = await Client.GetFromJsonAsync<RealmSettingsDto>(Endpoint, JsonOptions, ct);
        Assert.Null(cleared!.EmailBranding.FooterText);
        Assert.Equal("Realm", cleared.EmailBranding.SubjectPrefix);
    }

    [Fact]
    public async Task Realm_email_branding_rejects_oversized_copy()
    {
        var response = await Client.PatchAsJsonAsync(Endpoint, new UpdateRealmSettingsDto
        {
            EmailBranding = new UpdateEmailBrandingSettingsDto { Preheader = new string('x', 201) },
        }, JsonOptions, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
