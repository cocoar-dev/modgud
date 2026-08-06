using Modgud.Api.Features.Admin;

namespace Modgud.Tests.Unit.Api.Features;

public class PageDocumentValidatorTests
{
    [Fact]
    public void Login_AcceptsBoundedSchemaV4Document()
    {
        const string schema = """
        {
          "id":"auth-page","type":"page","schemaVersion":4,
          "children":[
            {"id":"card","type":"card","props":{},"children":[
              {"id":"username","type":"text-input","name":"username","props":{}},
              {"id":"submit","type":"button","props":{"action":"auth:login"}}
            ]}
          ]
        }
        """;

        Assert.True(PageDocumentValidator.Validate("login", schema, out var error), error);
    }

    [Fact]
    public void Login_AcceptsHostedOtpAndPageOwnedLanguageActions()
    {
        const string schema = """
        {
          "id":"auth-page","type":"page","schemaVersion":4,
          "children":[
            {"id":"email","type":"text-input","name":"email","props":{}},
            {"id":"send","type":"button","props":{"action":"auth:request-login-code"}},
            {"id":"code","type":"otp-input","name":"otpCode","props":{}},
            {"id":"verify","type":"button","props":{"action":"auth:verify-login-code"}},
            {"id":"resend","type":"button","props":{"action":"auth:resend-login-code"}},
            {"id":"back","type":"button","props":{"action":"auth:back-to-email"}},
            {"id":"language","type":"button","props":{"action":"auth:toggle-language"}}
          ]
        }
        """;

        Assert.True(PageDocumentValidator.Validate("login", schema, out var error), error);
    }

    [Fact]
    public void RejectsDisallowedHostAction()
    {
        const string schema = """
        {"id":"p","type":"page","schemaVersion":4,"children":[
          {"id":"x","type":"button","props":{"action":"admin:delete-realm"}}
        ]}
        """;

        Assert.False(PageDocumentValidator.Validate("login", schema, out var error));
        Assert.Contains("disallowed action", error);
    }

    [Fact]
    public void Consent_RequiresSecurityWarningInsideCard()
    {
        const string missingWarning = """
        {"id":"p","type":"page","schemaVersion":4,"children":[
          {"id":"consent-card","type":"card","props":{},"children":[]}
        ]}
        """;

        Assert.False(PageDocumentValidator.Validate("consent", missingWarning, out var error));
        Assert.Contains("unverified-client warning", error);
    }

    [Fact]
    public void Consent_AcceptsAllowlistedScopeRepeatAndSecurityWarning()
    {
        const string schema = """
        {"id":"p","type":"page","schemaVersion":4,"children":[
          {"id":"consent-card","type":"card","props":{},"children":[
            {"id":"unverified-client-warning","type":"note","props":{}},
            {"id":"scopes","type":"repeat","props":{"source":"consent.requestedScopes","maxItems":100},"children":[
              {"id":"scope","type":"checkbox","name":"$selection","props":{}}
            ]},
            {"id":"allow","type":"button","props":{"action":"auth:consent-allow"}}
          ]}
        ]}
        """;

        Assert.True(PageDocumentValidator.Validate("consent", schema, out var error), error);
    }
}
