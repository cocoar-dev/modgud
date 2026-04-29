using System.Net;

namespace Cocoar.Auth.Authentication.ExtensionMethods;

public static class HttpRequestExtensions
{
    public static List<IPAddress> FindSourceIp(this HttpRequest httpRequest)
    {


        var sourceIps = new List<IPAddress>();


        if (httpRequest.Headers.ContainsKey("X-Forwarded-For"))
        {
            var ips = httpRequest.Headers["X-Forwarded-For"];
            sourceIps = ips.OfType<string>().Select(IPAddress.Parse).ToList();
        }

        if (httpRequest.HttpContext.Connection.RemoteIpAddress != null)
        {
            sourceIps.Add(httpRequest.HttpContext.Connection.RemoteIpAddress);
        }
            


        return sourceIps;
    }

}