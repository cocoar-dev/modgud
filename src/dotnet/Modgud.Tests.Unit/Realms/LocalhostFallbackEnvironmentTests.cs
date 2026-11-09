using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.FileProviders;
using Modgud.Infrastructure.Realms;

namespace Modgud.Tests.Unit.Realms;

/// <summary>
/// F.routing-F-R3 — the single-realm localhost fallback is a development convenience.
/// In a deployed environment an unmatched host fails closed (404), as the concepts and
/// admin docs say; only Development and the test host keep the fallback.
/// </summary>
public class LocalhostFallbackEnvironmentTests
{
    [Theory]
    [InlineData("Development", true)]
    [InlineData("Testing", true)]
    [InlineData("Staging", false)]
    [InlineData("Production", false)]
    public void The_fallback_only_applies_outside_deployed_environments(string environment, bool allowed) =>
        Assert.Equal(allowed, RealmCacheLookup.AllowsLocalhostFallback(new Env(environment)));

    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Modgud";
        public string ContentRootPath { get; set; } = "/";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
