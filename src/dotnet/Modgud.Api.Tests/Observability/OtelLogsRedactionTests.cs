using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Serilog;
using Serilog.Sinks.OpenTelemetry;

namespace Modgud.Api.Tests.Observability;

/// <summary>
/// Phase 4 (§B.2) — the redaction GUARANTEE is proven end-to-end against a real
/// OTel Collector, not a config unit test. A log carrying PII is emitted through
/// the SAME Serilog → OTLP sink the app uses, into a collector running the SAME
/// redaction processor that ships, and the exported output is asserted to be
/// scrubbed before it would ever reach OpenObserve.
///
/// If the redaction processor is removed or misconfigured, this test fails — it
/// is the executable form of the "operationally conditional" guarantee.
///
/// Readback is the collector's debug exporter (stdout via GetLogsAsync): no bind
/// mount / writable volume, so it is portable across Windows/arm64 dev and the
/// amd64 CI runner.
/// </summary>
public class OtelLogsRedactionTests
{
    // arm64-safe contrib tag (the transform/OTTL processor is contrib-only).
    private const string CollectorImage = "otel/opentelemetry-collector-contrib:0.153.0";

    private static string TestConfigPath =>
        Path.Combine(AppContext.BaseDirectory, "Observability", "otel-collector-test-config.yaml");

    private static string ShippedConfigPath =>
        Path.Combine(AppContext.BaseDirectory, "Observability", "otel-collector-config.shipped.yaml");

    private const string RulesetStart = "# >>> redaction-ruleset: v2";
    private const string RulesetEnd = "# >>> end redaction-ruleset: v2 <<<";

    /// <summary>
    /// Anti-drift: the redaction block the e2e test exercises must be the one
    /// that ships. Cheap, no Docker — guards against the test passing on a stale
    /// copy of the rules.
    /// </summary>
    [Fact]
    public void RedactionRuleset_TestConfig_MatchesShipped()
    {
        var testBlock = ExtractRuleset(File.ReadAllText(TestConfigPath));
        var shippedBlock = ExtractRuleset(File.ReadAllText(ShippedConfigPath));

        Assert.False(string.IsNullOrWhiteSpace(testBlock), "test config has no redaction-ruleset block");
        Assert.Equal(shippedBlock, testBlock);
    }

    /// <summary>
    /// The block match above pins the processor DEFINITION; this pins the
    /// shipped pipeline WIRING. Without it, dropping transform/redaction from the
    /// shipped logs pipeline would leave both other tests green while a real
    /// collector exported PII un-redacted.
    /// </summary>
    [Fact]
    public void ShippedPipeline_WiresRedactionBeforeExport()
    {
        var shipped = File.ReadAllText(ShippedConfigPath);
        var procIdx = shipped.IndexOf("processors: [", StringComparison.Ordinal);
        Assert.True(procIdx >= 0, "shipped config has no pipeline processors list");

        var listEnd = shipped.IndexOf(']', procIdx);
        Assert.True(listEnd > procIdx, "malformed processors list in shipped config");
        var procList = shipped.Substring(procIdx, listEnd - procIdx);

        var redactionIdx = procList.IndexOf("transform/redaction", StringComparison.Ordinal);
        var batchIdx = procList.IndexOf("batch", StringComparison.Ordinal);
        Assert.True(redactionIdx >= 0, "shipped logs pipeline does not wire the redaction processor");
        Assert.True(batchIdx < 0 || redactionIdx < batchIdx,
            "redaction must run before batch/export in the shipped logs pipeline");
    }

    [Fact]
    public async Task PiiInLogs_IsRedactedByCollector_BeforeExport()
    {
        // PII samples — distinct, recognisable values we can assert disappeared.
        const string email = "john.doe@example.com";
        const string ipv4 = "203.0.113.45";
        const string ipv6 = "2001:db8::1";          // ::-compressed (leading group)
        const string ipv6Loopback = "::1";          // leading-:: form
        const string jwt = "eyJhbGciOiJIUzI1.eyJzdWIiOiIxMjM0NTY3.SflKxwRJSMeKK";
        const string bodyCred = "abc.def123";       // becomes "bearer abc.def123" in the body
        const string attrCred = "Bearer xyz.tok456"; // a credential carried in an attribute
        const string username = "bob_smith";        // non-email login id: in the body ("User=...") AND the UserName attribute
        const string timestamp = "12:34:56";        // must SURVIVE (not an IP)
        const string realm = "acme";                // must SURVIVE (the realm tag)
        const string serviceVersion = "1.0.0.0";    // resource attr, must SURVIVE

        await using var collector = new ContainerBuilder()
            .WithImage(CollectorImage)
            // Copy the config in via the Docker API (no host bind mount).
            .WithResourceMapping(File.ReadAllBytes(TestConfigPath), "/etc/otelcol-contrib/config.yaml")
            .WithPortBinding(4317, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Everything is ready"))
            .Build();

        await collector.StartAsync();
        var otlpEndpoint = $"http://localhost:{collector.GetMappedPublicPort(4317)}";

        // Emit through the REAL sink: Serilog → OTLP gRPC → collector. The
        // resource attributes mirror Program.cs (incl. service.instance.id); the
        // Realm/UserName properties mirror what the app stamps.
        using (var logger = new LoggerConfiguration()
                   .MinimumLevel.Information()
                   .WriteTo.OpenTelemetry(o =>
                   {
                       o.Endpoint = otlpEndpoint;
                       o.Protocol = OtlpProtocol.Grpc;
                       o.ResourceAttributes = new Dictionary<string, object>
                       {
                           ["service.name"] = "modgud-redaction-test",
                           ["service.version"] = serviceVersion,
                           ["service.instance.id"] = Environment.MachineName,
                       };
                   })
                   .CreateLogger())
        {
            logger
                .ForContext("Realm", realm)
                .ForContext("Email", email)
                .ForContext("ClientIp", ipv4)
                .ForContext("V6", ipv6)
                .ForContext("V6Loopback", ipv6Loopback)
                .ForContext("Authorization", attrCred)      // redacted by the attr bearer rule
                // {UserName} renders into the "User=" body (body rule) and also
                // becomes the UserName attribute (dropped by delete_key).
                .Information(
                    "Login failed for {Email} from {ClientIp} bearer {Cred} jwt {Jwt} at {Time} v6 {V6} lo {V6Lo} User={UserName}",
                    email, ipv4, bodyCred, jwt, timestamp, ipv6, ipv6Loopback, username);
        } // dispose flushes the OTLP sink

        var exported = await PollForExportedRecordAsync(collector);

        // --- PII must be gone (body AND attributes) ---
        Assert.DoesNotContain(email, exported);
        Assert.DoesNotContain(ipv4, exported);
        Assert.DoesNotContain(ipv6, exported);
        Assert.DoesNotContain(ipv6Loopback, exported); // leading-:: form covered
        Assert.DoesNotContain(jwt, exported);
        Assert.DoesNotContain("bearer " + bodyCred, exported);
        Assert.DoesNotContain("xyz.tok456", exported); // credential in an attribute
        Assert.DoesNotContain(username, exported);     // username: body "User=" + UserName attribute

        // --- redaction markers must be present ---
        Assert.Contains("[REDACTED_EMAIL]", exported);
        Assert.Contains("[REDACTED_IP]", exported);
        Assert.Contains("[REDACTED_TOKEN]", exported);
        Assert.Contains("[REDACTED_AUTHORIZATION]", exported);
        Assert.Contains("[REDACTED_USER]", exported);

        // --- non-PII must survive (no over-redaction) ---
        Assert.Contains(timestamp, exported);      // HH:MM:SS is not an IP
        Assert.Contains(realm, exported);          // realm tag travels through
        Assert.Contains(serviceVersion, exported); // service.version not nuked as IPv4
    }

    /// <summary>
    /// Sink dispose flushed; the collector receives over gRPC, batches (1s) and
    /// prints the post-redaction record to stdout via the debug exporter. Poll
    /// the container logs until that record appears.
    /// </summary>
    private static async Task<string> PollForExportedRecordAsync(IContainer collector)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var (stdout, stderr) = await collector.GetLogsAsync();
            var combined = stdout + "\n" + stderr;
            // The debug exporter prints "Body: Str(...)" only for an exported record.
            if (combined.Contains("Body: Str("))
            {
                return combined;
            }

            await Task.Delay(500);
        }

        var (finalOut, finalErr) = await collector.GetLogsAsync();
        throw new Xunit.Sdk.XunitException(
            "Collector exported no log record within 30s.\n--- stdout ---\n" + finalOut +
            "\n--- stderr ---\n" + finalErr);
    }

    private static string ExtractRuleset(string yaml)
    {
        var start = yaml.IndexOf(RulesetStart, StringComparison.Ordinal);
        var end = yaml.IndexOf(RulesetEnd, StringComparison.Ordinal);
        if (start < 0 || end < 0)
        {
            return string.Empty;
        }

        // Normalise line endings so a CRLF/LF checkout difference between the two
        // files can't fail an otherwise-identical block.
        return yaml.Substring(start, end - start + RulesetEnd.Length).Replace("\r\n", "\n");
    }
}
