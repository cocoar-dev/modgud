using System.Net;

namespace Modgud.Api.Tests.Security;

/// <summary>
/// B6-SAML-05 — the "IdP Entity ID" field ("set explicitly only if the metadata omits it
/// or you need to override") was persisted but never read: metadata without an entityID
/// was rejected outright, and the accepted issuer always came from the metadata. The
/// override now applies when the metadata is parsed, so it is the issuer everywhere.
/// </summary>
public partial class SamlAcsReplayEndToEndTests
{
    [Fact]
    public async Task An_entity_id_override_makes_metadata_without_one_usable()
    {
        var ct = TestContext.Current.CancellationToken;
        using var idpCert = CreateIdpSigningCert();
        var provider = await CreateAndRegisterProviderAsync(idpCert,
            entityIdOverride: IdpEntityId, metadataWithoutEntityId: true);
        var (userId, _) = await SeedLinkedUserAsync(provider.Id);

        using var http = NewBrowser();
        var requestId = await StartRequestAsync(http, provider.Id, ct);
        var response = await PostToAcsAsync(http, MintSignedResponse(idpCert, requestId), ct);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", response.Headers.Location!.ToString());
        await AssertSignedInAsync(http, userId, ct);
    }
}
