using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Modgud.Api.Helper;
using Serilog;

namespace Modgud.Tests.Unit.Observability;

/// <summary>
/// B9-J01 — the request-log event is exported as-is (Serilog OTLP sink: every property
/// becomes a log attribute), and the collector cannot recognise an opaque link secret.
/// So the producer must never carry a query value or a user name.
/// </summary>
public class RequestLogEnrichmentTests
{
    private sealed class Capture : IDiagnosticContext
    {
        public Dictionary<string, object?> Properties { get; } = new();
        public void Set(string propertyName, object? value, bool destructureObjects = false) => Properties[propertyName] = value;
        public void SetException(Exception exception) { }
    }

    private static Dictionary<string, object?> Enrich(string query, ClaimsPrincipal? user = null)
    {
        var http = new DefaultHttpContext();
        http.Request.Host = new HostString("auth.example.test");
        http.Request.Path = "/magic-login";
        http.Request.QueryString = new QueryString(query);
        if (user is not null) http.User = user;
        var capture = new Capture();
        LogHelper.EnrichFromRequest(capture, http);
        return capture.Properties;
    }

    [Theory]
    [InlineData("?userId=6f1c&token=SECRET-LINK-TOKEN")]                    // magic link / password reset
    [InlineData("?type=account&token=SECRET-LINK-TOKEN")]                   // e-mail verification
    [InlineData("?ticket=SECRET-LINK-TOKEN")]                               // consent / device ticket
    [InlineData("?code=SECRET-LINK-TOKEN&state=SECRET-LINK-TOKEN")]         // any other carrier
    [InlineData("?SECRET-LINK-TOKEN")]                                      // a bare value without a key
    public void Query_values_never_reach_the_request_log(string query)
    {
        var properties = Enrich(query);

        var logged = Assert.IsType<string>(properties["QueryString"]);
        Assert.DoesNotContain("SECRET-LINK-TOKEN", logged);
        Assert.DoesNotContain("6f1c", logged);
        Assert.DoesNotContain("account", logged);
        Assert.All(properties.Values, value => Assert.DoesNotContain("SECRET-LINK-TOKEN", value?.ToString() ?? ""));
    }

    [Fact]
    public void Parameter_names_stay_for_diagnosis()
    {
        var logged = Assert.IsType<string>(Enrich("?userId=6f1c&token=x&token=y")["QueryString"]);
        Assert.Equal("?userId=[redacted]&token=[redacted]&token=[redacted]", logged);
    }

    [Fact]
    public void No_query_no_property()
        => Assert.False(Enrich("").ContainsKey("QueryString"));

    [Fact]
    public void The_user_is_logged_by_id_not_by_name()
    {
        var id = Guid.NewGuid();
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, id.ToString()), new Claim(ClaimTypes.Name, "alice.private")], "Test"));

        var properties = Enrich("", user);

        Assert.False(properties.ContainsKey("User"));
        Assert.Equal(id.ToString(), properties["UserId"]);
        Assert.All(properties.Values, value => Assert.DoesNotContain("alice.private", value?.ToString() ?? ""));
    }

    [Fact]
    public void An_anonymous_request_is_logged_as_anonymous()
        => Assert.Equal("anonymous", Enrich("")["UserId"]);
}
