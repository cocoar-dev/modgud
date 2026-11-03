using System.Text.RegularExpressions;
using Modgud.Authentication.ExtensionMethods;
using Serilog;

namespace Modgud.Api.Helper;

public static partial class LogHelper
{
    public static void EnrichFromRequest(IDiagnosticContext diagnosticContext, HttpContext httpContext)
    {
        var request = httpContext.Request;


        // Set all the common properties available for every request
        diagnosticContext.Set("Host", request.Host);
        diagnosticContext.Set("Protocol", request.Protocol);
        diagnosticContext.Set("Scheme", request.Scheme?.ToLower());

        // B9-J01 — the id, not the user name: the request log is exported as-is and a
        // plain user name is not one of the collector's redaction patterns.
        diagnosticContext.Set("UserId", httpContext.GetUserId()?.ToString() ?? "anonymous");

        // B9-J01 — links carry one-time secrets in the query (magic-login, reset-password,
        // verify-email, bootstrap, install, consent/device tickets). The collector cannot
        // recognise an opaque token, so no query value is ever logged; names stay.
        if (request.QueryString.HasValue)
        {
            diagnosticContext.Set("QueryString", RedactQuery(request.QueryString.Value!));
        }

        // Set the content-type of the Response at this point
        diagnosticContext.Set("ContentType", httpContext.Response.ContentType);

        // Retrieve the IEndpointFeature selected for the request
        var endpoint = httpContext.GetEndpoint();
        if (endpoint is object) // endpoint != null
        {
            diagnosticContext.Set("EndpointName", endpoint.DisplayName);
        }
    }

    /// <summary>Keeps the parameter names of a query and replaces every value. A segment
    /// without '=' or with an unusual name is itself a value and is replaced whole.</summary>
    internal static string RedactQuery(string query)
    {
        var segments = query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(segment =>
            {
                var equals = segment.IndexOf('=');
                return equals > 0 && SafeName().IsMatch(segment[..equals])
                    ? $"{segment[..equals]}=[redacted]"
                    : "[redacted]";
            });
        return "?" + string.Join('&', segments);
    }

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_.\-]{0,39}$")]
    private static partial Regex SafeName();
}
