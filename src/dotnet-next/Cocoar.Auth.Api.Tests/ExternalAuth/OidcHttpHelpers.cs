using System.Net;
using System.Text.RegularExpressions;

namespace Cocoar.Auth.Api.Tests.ExternalAuth;

/// <summary>
/// <see cref="DelegatingHandler"/> that plugs a shared <see cref="CookieContainer"/>
/// into a <see cref="WebApplicationFactory{T}"/>-created HttpClient. We reuse
/// the same container across the Cocoar.Auth client (TestServer) and the TestIdP
/// client (real Kestrel) so cookies flow naturally across the OIDC redirect
/// chain — the browsers' behavior.
/// </summary>
internal sealed class SharedCookieHandler(CookieContainer cookies) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var header = cookies.GetCookieHeader(request.RequestUri!);
        if (!string.IsNullOrEmpty(header))
            request.Headers.Add("Cookie", header);

        var response = await base.SendAsync(request, cancellationToken);

        // Iterate all header entries rather than using TryGetValues("Set-Cookie")
        // — TestServer occasionally surfaces Set-Cookie with a case mismatch or
        // outside the standard accessor.
        foreach (var h in response.Headers)
        {
            if (!string.Equals(h.Key, "Set-Cookie", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var cookie in h.Value)
                cookies.SetCookies(request.RequestUri!, cookie);
        }

        return response;
    }
}

internal static class OidcFlowExtensions
{
    /// <summary>
    /// Expects the response to be a 302/303 redirect and returns the <c>Location</c>.
    /// Throws with useful diagnostics when the caller hits a surprise status code
    /// (common when something's wrong — e.g. a 500 during /authorize).
    /// </summary>
    public static Uri ExpectRedirect(this HttpResponseMessage response, string? stage = null)
    {
        if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308))
        {
            var body = response.Content.ReadAsStringAsync().Result;
            throw new Xunit.Sdk.XunitException(
                $"Expected redirect at stage '{stage ?? "?"}' but got {(int)response.StatusCode} {response.StatusCode}.\nBody:\n{Truncate(body, 2000)}");
        }
        var location = response.Headers.Location
            ?? throw new Xunit.Sdk.XunitException($"Redirect response at stage '{stage ?? "?"}' has no Location header.");
        return location;
    }

    /// <summary>
    /// Handles both redirect-style and form_post-style responses from the
    /// authorize endpoint. On form_post, OpenIddict returns an HTML form that a
    /// real browser would auto-submit; here we parse it and return the target
    /// URL + the form fields the caller needs to POST.
    /// </summary>
    public static (HttpMethod Method, Uri Target, Dictionary<string, string> Fields) ParseAuthorizeResponse(
        HttpResponseMessage response, string html, string? stage = null)
    {
        if ((int)response.StatusCode is 302 or 303)
        {
            var location = response.Headers.Location
                ?? throw new Xunit.Sdk.XunitException($"Redirect at '{stage}' has no Location.");
            return (HttpMethod.Get, location, new Dictionary<string, string>());
        }

        if (response.StatusCode == HttpStatusCode.OK && html.Contains("<form", StringComparison.OrdinalIgnoreCase))
        {
            var actionMatch = Regex.Match(html, "<form[^>]*action=\"([^\"]+)\"", RegexOptions.IgnoreCase);
            if (!actionMatch.Success)
                throw new Xunit.Sdk.XunitException($"form_post at '{stage}' has no action URL in body:\n{Truncate(html, 500)}");

            var fields = new Dictionary<string, string>();
            foreach (Match m in Regex.Matches(html,
                "<input[^>]*name=\"([^\"]+)\"[^>]*value=\"([^\"]*)\"", RegexOptions.IgnoreCase))
            {
                fields[WebUtility.HtmlDecode(m.Groups[1].Value)] = WebUtility.HtmlDecode(m.Groups[2].Value);
            }
            return (HttpMethod.Post, new Uri(WebUtility.HtmlDecode(actionMatch.Groups[1].Value)), fields);
        }

        throw new Xunit.Sdk.XunitException(
            $"Unexpected authorize response at '{stage}': {(int)response.StatusCode} — body:\n{Truncate(html, 1000)}");
    }

    /// <summary>Extract a hidden form field value from a server-rendered HTML page.</summary>
    public static string? ExtractHiddenFormField(string html, string fieldName)
    {
        var pattern = $"<input\\s+type=\"hidden\"\\s+name=\"{Regex.Escape(fieldName)}\"\\s+value=\"([^\"]*)\"";
        var match = Regex.Match(html, pattern, RegexOptions.IgnoreCase);
        return match.Success ? WebUtility.HtmlDecode(match.Groups[1].Value) : null;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
