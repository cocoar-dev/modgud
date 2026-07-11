using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using ITfoxtec.Identity.Saml2;
using ITfoxtec.Identity.Saml2.MvcCore;
using ITfoxtec.Identity.Saml2.Schemas;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Modgud.Authentication.Domain;
using Modgud.Authentication.Identity.LoginProviders.Saml;
using Modgud.Authentication.Sessions;
using Modgud.Infrastructure.Audit;
using Modgud.Infrastructure.Observability;

namespace Modgud.Authentication.Api.ExternalAuth.Saml;

/// <summary>
/// Per-request SAML SP protocol implementation: builds + signs the
/// outbound AuthnRequest, parses + validates the inbound SAMLResponse,
/// extracts claims into the OIDC-shaped <see cref="ClaimsPrincipal"/>
/// that <see cref="ExternalLoginProcessor"/> already knows how to handle,
/// and renders the SP metadata XML.
/// <para>
/// One service centralises all three operations so endpoint handlers
/// stay thin and the protocol concerns (signing, audience, RelayState,
/// claim-map application, NameID-to-sub translation) live in one place.
/// </para>
/// </summary>
public class SamlLoginFlow(
    SamlContextBuilder contextBuilder,
    SamlSpCertificateService spCertService,
    ExternalLoginProcessor processor,
    SignInManager<ApplicationUser> signInManager,
    ISessionService sessionService,
    ISecurityAuditLog securityAudit,
    ILogger<SamlLoginFlow> logger)
{
    /// <summary>
    /// Subject value we stamp on the External principal's <c>iss</c> claim
    /// when the IdP doesn't include an explicit Issuer (defensive — every
    /// real SAML assertion has one, but the lib's ClaimsIdentity might
    /// omit it depending on lib version).
    /// </summary>
    private const string MissingIssuer = "saml:unknown-issuer";

    /// <summary>
    /// SP-initiated login: build a (possibly signed) AuthnRequest, redirect
    /// the browser to the IdP's SSO endpoint via HTTP-Redirect binding.
    /// <paramref name="returnUrl"/> rides RelayState round-trip so the ACS
    /// callback knows where to send the user after sign-in.
    /// </summary>
    public async Task<IResult> StartLoginAsync(
        RegisteredSamlProvider provider,
        string? returnUrl,
        CancellationToken ct)
    {
        if (provider.IdpMetadata is null)
        {
            securityAudit.Record(new SecurityAuditRecord
            {
                EventType = AuditEvents.ExternalLoginRejected,
                Level = "Warning",
                Status = "rejected",
                Reason = $"SAML: no IdP metadata cached (provider {provider.LoginProviderId})",
                Message = $"SAML login refused for provider {provider.Slug} — no IdP metadata cached",
            });
            return Results.Redirect("/login?error=saml-no-metadata");
        }

        if (string.IsNullOrEmpty(provider.IdpMetadata.SsoRedirectUrl)
            && string.IsNullOrEmpty(provider.IdpMetadata.SsoPostUrl))
        {
            securityAudit.Record(new SecurityAuditRecord
            {
                EventType = AuditEvents.ExternalLoginRejected,
                Level = "Warning",
                Status = "rejected",
                Reason = $"SAML: IdP metadata has no SSO endpoint (provider {provider.LoginProviderId})",
                Message = $"SAML login refused for provider {provider.Slug} — IdP metadata has no SSO endpoint",
            });
            return Results.Redirect("/login?error=saml-no-sso");
        }

        using var ctx = await contextBuilder.BuildAsync(provider, ct);
        var ssoUrl = provider.IdpMetadata.SsoRedirectUrl ?? provider.IdpMetadata.SsoPostUrl!;
        var acsUrl = contextBuilder.BuildAcsUrl(provider.Slug);

        var authnRequest = new Saml2AuthnRequest(ctx.Configuration)
        {
            ForceAuthn = false,
            IsPassive = false,
            AssertionConsumerServiceUrl = new Uri(acsUrl),
            Destination = new Uri(ssoUrl),
            NameIdPolicy = new NameIdPolicy
            {
                AllowCreate = true,
                Format = provider.FlavorData.NameIdFormat,
            },
        };

        var binding = new Saml2RedirectBinding();
        if (!string.IsNullOrEmpty(returnUrl))
            binding.RelayState = returnUrl;

        binding.Bind(authnRequest);

        logger.LogInformation(
            "SAML AuthnRequest built for provider {Id} → IdP {IdpEntity}",
            provider.LoginProviderId, provider.IdpMetadata.EntityId);

        return Results.Redirect(binding.RedirectLocation.OriginalString);
    }

    /// <summary>
    /// AssertionConsumerService: receive the IdP's SAMLResponse via the
    /// browser form-POST, validate it, extract claims, hand off to
    /// <see cref="ExternalLoginProcessor"/>, sign in via SignInManager,
    /// redirect to RelayState's <c>returnUrl</c>.
    /// </summary>
    public async Task<IResult> HandleAcsAsync(
        RegisteredSamlProvider provider,
        HttpContext http,
        CancellationToken ct)
    {
        if (provider.IdpMetadata is null)
            return Results.Redirect("/login?error=saml-no-metadata");

        var ip = http.Connection.RemoteIpAddress?.ToString();

        Saml2AuthnResponse saml2Response;
        Saml2PostBinding binding;
        Saml2RequestContext ctx;
        try
        {
            ctx = await contextBuilder.BuildAsync(provider, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "SAML context build failed for provider {Id}",
                provider.LoginProviderId);
            securityAudit.Record(new SecurityAuditRecord
            {
                EventType = AuditEvents.ExternalLoginRejected,
                Level = "Warning",
                Ip = ip,
                Status = "rejected",
                Reason = $"SAML: context build failed (provider {provider.LoginProviderId})",
                Message = $"SAML login refused for provider {provider.Slug} — context build failed",
            });
            ModgudMeters.RecordLogin(ModgudMeters.LoginMethod.External, ModgudMeters.LoginOutcome.Failure);
            return Results.Redirect("/login?error=saml-invalid");
        }

        // Take ownership of the disposable context for the rest of the
        // request — every exit path below must dispose it to return the
        // native cert handles to the OS.
        using var _ctx = ctx;
        try
        {
            binding = new Saml2PostBinding();
            saml2Response = new Saml2AuthnResponse(ctx.Configuration);
            binding.ReadSamlResponse(http.Request.ToGenericHttpRequest(), saml2Response);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "SAML response read/validate failed for provider {Id}",
                provider.LoginProviderId);
            securityAudit.Record(new SecurityAuditRecord
            {
                EventType = AuditEvents.ExternalLoginRejected,
                Level = "Warning",
                Ip = ip,
                Status = "rejected",
                Reason = $"SAML: response read/validate failed (provider {provider.LoginProviderId})",
                Message = $"SAML login refused for provider {provider.Slug} — response read/validate failed",
            });
            ModgudMeters.RecordLogin(ModgudMeters.LoginMethod.External, ModgudMeters.LoginOutcome.Failure);
            return Results.Redirect("/login?error=saml-invalid");
        }

        if (saml2Response.Status != Saml2StatusCodes.Success)
        {
            securityAudit.Record(new SecurityAuditRecord
            {
                EventType = AuditEvents.ExternalLoginRejected,
                Level = "Warning",
                Ip = ip,
                Status = "rejected",
                Reason = $"SAML: non-success status {saml2Response.Status} (provider {provider.LoginProviderId})",
                Message = $"SAML login refused for provider {provider.Slug} — non-success status {saml2Response.Status}",
            });
            ModgudMeters.RecordLogin(ModgudMeters.LoginMethod.External, ModgudMeters.LoginOutcome.Failure);
            return Results.Redirect($"/login?error=saml-{Uri.EscapeDataString(saml2Response.Status.ToString() ?? "status")}");
        }

        // ITfoxtec validates signatures that ARE present against
        // SignatureValidationCertificates but does not require signatures to be
        // present — an IdP could send an unsigned Response/Assertion and
        // ReadSamlResponse would happily return. Enforce the admin-configured
        // FlavorData toggles by post-checking the XML for Signature elements
        // at the expected levels.
        var sigError = CheckRequiredSignatures(saml2Response.XmlDocument, provider.FlavorData);
        if (sigError is not null)
        {
            securityAudit.Record(new SecurityAuditRecord
            {
                EventType = AuditEvents.SamlSignatureRejected,
                Level = "Warning",
                Ip = ip,
                Status = "rejected",
                Reason = $"SAML: required-signature check failed ({sigError}) for provider {provider.LoginProviderId}",
                Message = $"SAML response failed required-signature check ({sigError}) for provider {provider.Slug}",
            });
            ModgudMeters.RecordLogin(ModgudMeters.LoginMethod.External, ModgudMeters.LoginOutcome.Failure);
            return Results.Redirect($"/login?error=saml-{Uri.EscapeDataString(sigError)}");
        }

        // ITfoxtec's response builds its own ClaimsIdentity from the
        // assertion. We translate that into the OIDC-shaped principal
        // ExternalLoginProcessor expects (iss/sub + attribute claims under
        // logical names per the configured AttributeMap).
        var external = BuildExternalPrincipal(provider, saml2Response);

        // Detect link-flow (user already authenticated via the app cookie).
        //
        // KNOWN LIMITATION: the Modgud.Auth cookie is set with SameSite=Lax
        // (Program.cs ApplicationScheme config), and the IdP ACS POST is
        // cross-site, so this AuthenticateAsync returns Failed even when the
        // user is in fact logged in. The link-flow then degrades to JIT /
        // email-auto-link. Fix needs server-side state — see
        // dev-docs/future-features/saml-link-flow-samesite.md. Blocked on
        // test-server + real-IdP verification.
        var existingAuth = await http.AuthenticateAsync(IdentityConstants.ApplicationScheme);
        Guid? authenticatedUserId = null;
        if (existingAuth.Succeeded && existingAuth.Principal is not null)
        {
            var idClaim = existingAuth.Principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (Guid.TryParse(idClaim, out var parsed)) authenticatedUserId = parsed;
        }

        var result = await processor.ProcessAsync(external, provider.LoginProviderId, ct, authenticatedUserId);

        if (!result.Succeeded)
        {
            ModgudMeters.RecordLogin(ModgudMeters.LoginMethod.External, ModgudMeters.LoginOutcome.Failure);
            var code = Uri.EscapeDataString(result.ErrorCode ?? "unknown");
            return Results.Redirect($"/login?error={code}");
        }

        await http.SignInAsync(
            IdentityConstants.ApplicationScheme,
            result.Principal!,
            new AuthenticationProperties { IsPersistent = true });

        ModgudMeters.RecordLogin(ModgudMeters.LoginMethod.External, ModgudMeters.LoginOutcome.Success);

        var signedInIdClaim = result.Principal!.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (Guid.TryParse(signedInIdClaim, out var signedInUserId))
            await SessionTracker.RecordLoginAsync(sessionService, http, signedInUserId, ct);

        var returnUrl = ExtractRelayStateReturnUrl(binding);
        return Results.Redirect(string.IsNullOrWhiteSpace(returnUrl) ? "/" : returnUrl);
    }

    /// <summary>
    /// SP metadata XML for the given provider. Customer pastes the URL to
    /// this endpoint into their IdP's SP-config screen. Includes the active
    /// SP cert (and previous-during-overlap) as both Signing and Encryption
    /// key descriptors — most IdPs are happy with one cert serving both roles.
    /// </summary>
    public async Task<string> BuildSpMetadataAsync(
        RegisteredSamlProvider provider,
        CancellationToken ct)
    {
        var spEntityId = contextBuilder.BuildSpEntityId(provider.Slug);
        var acsUrl = contextBuilder.BuildAcsUrl(provider.Slug);
        // GetMetadataCertsAsync hands us X509Certificate2 instances with
        // native key handles. The metadata XML only needs the public-cert
        // bytes (via cert.Export), so dispose them as soon as we've exported
        // — this endpoint is AllowAnonymous and a scraper hammering it could
        // otherwise drain handles. try/finally guarantees disposal even when
        // XDocument construction throws.
        var certs = await spCertService.GetMetadataCertsAsync(ct);
        try
        {
            XNamespace md = "urn:oasis:names:tc:SAML:2.0:metadata";
            XNamespace ds = "http://www.w3.org/2000/09/xmldsig#";

            var keyDescriptors = new List<XElement>();
            foreach (var cert in certs)
            {
                foreach (var use in new[] { "signing", "encryption" })
                {
                    keyDescriptors.Add(new XElement(md + "KeyDescriptor",
                        new XAttribute("use", use),
                        new XElement(ds + "KeyInfo",
                            new XElement(ds + "X509Data",
                                new XElement(ds + "X509Certificate",
                                    Convert.ToBase64String(cert.Export(X509ContentType.Cert)))))));
                }
            }

            var doc = new XDocument(
                new XElement(md + "EntityDescriptor",
                    new XAttribute("entityID", spEntityId),
                    new XAttribute(XNamespace.Xmlns + "md", md.NamespaceName),
                    new XAttribute(XNamespace.Xmlns + "ds", ds.NamespaceName),
                    new XElement(md + "SPSSODescriptor",
                        new XAttribute("AuthnRequestsSigned", provider.FlavorData.SignAuthnRequest ? "true" : "false"),
                        new XAttribute("WantAssertionsSigned", provider.FlavorData.WantAssertionsSigned ? "true" : "false"),
                        new XAttribute("protocolSupportEnumeration", "urn:oasis:names:tc:SAML:2.0:protocol"),
                        keyDescriptors,
                        new XElement(md + "NameIDFormat", provider.FlavorData.NameIdFormat),
                        new XElement(md + "AssertionConsumerService",
                            new XAttribute("Binding", "urn:oasis:names:tc:SAML:2.0:bindings:HTTP-POST"),
                            new XAttribute("Location", acsUrl),
                            new XAttribute("index", "0"),
                            new XAttribute("isDefault", "true")))));

            return doc.ToString();
        }
        finally
        {
            foreach (var cert in certs)
            {
                try { cert.Dispose(); }
                catch { /* defensive — disposal never throws */ }
            }
        }
    }

    private static ClaimsPrincipal BuildExternalPrincipal(
        RegisteredSamlProvider provider,
        Saml2AuthnResponse saml2Response)
    {
        var claims = new List<Claim>();

        // Issuer = IdP EntityID from the cached metadata (the signature on
        // the response already proved we're talking to the right IdP).
        var idpIssuer = provider.IdpMetadata?.EntityId ?? MissingIssuer;
        claims.Add(new Claim("iss", idpIssuer));

        // Subject = NameID, surfaced as ClaimTypes.NameIdentifier on the
        // ITfoxtec-built identity.
        var nameId = saml2Response.ClaimsIdentity?.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? string.Empty;
        if (!string.IsNullOrEmpty(nameId))
            claims.Add(new Claim("sub", nameId));

        // Pull every assertion claim into the principal as-is; the
        // ExternalLoginProcessor's ExtractRawClaims walks all claims into
        // a dict for the user-update script (and the AttributeMap-keyed
        // translation happens via the script).
        if (saml2Response.ClaimsIdentity is { } id)
        {
            foreach (var c in id.Claims)
                claims.Add(c);
        }

        // Plus the logical-name claims derived from FlavorData.AttributeMap.
        // The IdP's wire-format claim URIs vary (Microsoft uses long URIs,
        // some vendors short names); the AttributeMap normalises them so
        // the user-update script sees stable names like `email`, `groups`.
        var rawClaimMap = saml2Response.ClaimsIdentity?.Claims
            .GroupBy(c => c.Type)
            .ToDictionary(g => g.Key, g => g.Select(c => c.Value).ToArray())
            ?? new Dictionary<string, string[]>();

        foreach (var (logicalName, samlUris) in provider.FlavorData.AttributeMap)
        {
            foreach (var uri in samlUris)
            {
                if (rawClaimMap.TryGetValue(uri, out var values))
                {
                    foreach (var v in values)
                        claims.Add(new Claim(logicalName, v));
                    break; // First matching URI wins.
                }
            }
        }

        var identity = new ClaimsIdentity(claims, "saml");
        return new ClaimsPrincipal(identity);
    }

    /// <summary>
    /// Post-parse signature enforcement. ITfoxtec does NOT require signatures
    /// to be present in the response — it only validates whatever is there
    /// against <see cref="Saml2Configuration.SignatureValidationCertificates"/>.
    /// So if the admin has configured WantResponseSigned / WantAssertionsSigned
    /// we have to check the XML ourselves for the presence of the relevant
    /// <c>ds:Signature</c> element. Returns a short error-tag suitable for the
    /// redirect query string when enforcement fails; null when OK.
    /// </summary>
    private static string? CheckRequiredSignatures(XmlDocument? doc, SamlFlavorData flavor)
    {
        if (!flavor.WantResponseSigned && !flavor.WantAssertionsSigned) return null;
        if (doc?.DocumentElement is null) return "missing-document";

        var ns = new XmlNamespaceManager(doc.NameTable);
        ns.AddNamespace("samlp", "urn:oasis:names:tc:SAML:2.0:protocol");
        ns.AddNamespace("saml", "urn:oasis:names:tc:SAML:2.0:assertion");
        ns.AddNamespace("ds", "http://www.w3.org/2000/09/xmldsig#");

        if (flavor.WantResponseSigned)
        {
            // The Response-level signature must be a direct child of the
            // <samlp:Response> root. A signature anywhere else (e.g. only on
            // the Assertion) does NOT count — XML-signature-wrapping attacks
            // hinge exactly on that distinction.
            var responseSig = doc.DocumentElement.SelectSingleNode("ds:Signature", ns);
            if (responseSig is null) return "response-unsigned";
        }

        if (flavor.WantAssertionsSigned)
        {
            // Every Assertion in the response (typically one, but the spec
            // allows multiple) must carry its own ds:Signature element. The
            // wrapping defense is: per-assertion signature blocks an attacker
            // from gluing a stolen wrapper signature onto a fresh assertion.
            var assertions = doc.DocumentElement.SelectNodes("saml:Assertion", ns);
            if (assertions is null || assertions.Count == 0) return "assertion-missing";
            foreach (XmlNode assertion in assertions)
            {
                var assertionSig = assertion.SelectSingleNode("ds:Signature", ns);
                if (assertionSig is null) return "assertion-unsigned";
            }
        }

        return null;
    }

    private static string? ExtractRelayStateReturnUrl(Saml2PostBinding binding)
    {
        // SAML 2.0 RelayState SHOULD be ≤80 bytes and is opaque to the IdP.
        // We round-trip the returnUrl through it. If a malicious actor
        // tampers (it's not signed in HTTP-Redirect binding for IdP-side
        // signing), the worst they can do is redirect to a different
        // path-within-our-host, which Results.Redirect already constrains
        // when the value is relative. Reject absolute URLs to prevent
        // open-redirect.
        var raw = binding.RelayState;
        if (string.IsNullOrWhiteSpace(raw)) return null;

        if (Uri.TryCreate(raw, UriKind.Absolute, out _)) return null;
        // Same-origin absolute path only (rejects //…, /\…, and control-char
        // smuggling like /\t/evil.com that a browser collapses to //evil.com).
        return Modgud.Authentication.Api.LoginRedirectGuard.IsSameOriginPath(raw) ? raw : null;
    }
}
