using Microsoft.AspNetCore.Http;
using Modgud.Api.Middleware;

namespace Modgud.Tests.Unit.Api.Middleware;

public class SecurityHeadersMiddlewareTests
{
    [Fact]
    public async Task SpaDocument_KeepsStrictScriptPolicy()
    {
        var csp = await PolicyFor("/login");

        Assert.Contains("script-src 'self'", csp);
        Assert.DoesNotContain("unsafe-eval", csp);
        Assert.Contains("worker-src 'self' blob:", csp);
    }

    [Fact]
    public async Task PageRuntimeWorker_GetsEvalOnlyOnItsOwnResponse()
    {
        var csp = await PolicyFor("/assets/pageScriptRuntime.worker-CEyzE8OZ.js");

        Assert.Contains("script-src 'self' 'unsafe-eval'", csp);
        Assert.DoesNotContain("script-src 'self' 'unsafe-inline'", csp);
    }

    [Fact]
    public async Task UnrelatedWorker_DoesNotReceiveEvalPermission()
    {
        var csp = await PolicyFor("/assets/editor.worker-example.js");

        Assert.Contains("script-src 'self'", csp);
        Assert.DoesNotContain("unsafe-eval", csp);
    }

    private static Task<string> PolicyFor(string path) => Task.FromResult(
        SecurityHeadersMiddleware.BuildContentSecurityPolicy(false, new PathString(path)));
}
