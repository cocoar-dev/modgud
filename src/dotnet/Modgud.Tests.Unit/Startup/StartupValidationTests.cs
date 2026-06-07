using Modgud.Api;
using Modgud.Api.Startup;

namespace Modgud.Tests.Unit.Startup;

/// <summary>
/// Stage 0 of the cold-start ladder (no silent failures). Pins the fail-loud
/// guard for required configuration: a missing DB connection string — the exact
/// shape an unbound/mis-cased env override leaves behind — must surface a clear,
/// actionable error at startup, not boot far and die with a cryptic database
/// error far from the cause.
/// </summary>
public class StartupValidationTests
{
    private static StartUpConfiguration ConfWith(string connectionString) => new()
    {
        DbSettings = { ConnectionString = connectionString },
    };

    [Fact]
    public void Passes_when_connection_string_is_present()
    {
        var conf = ConfWith("Host=localhost;Database=modgud;Username=postgres;Password=postgres");

        // No throw == valid.
        StartupValidation.ValidateRequiredConfig(conf);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void Throws_a_clear_error_when_connection_string_is_missing(string connectionString)
    {
        var conf = ConfWith(connectionString);

        var ex = Assert.Throws<InvalidOperationException>(
            () => StartupValidation.ValidateRequiredConfig(conf));

        // The message must name the offending key so an operator can act on it —
        // that is the entire point of the guard.
        Assert.Contains("DbSettings", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ConnectionString", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Default_configuration_has_an_empty_connection_string_and_is_rejected()
    {
        // StartUpConfiguration.DbSettings.ConnectionString defaults to
        // string.Empty — precisely the state a mis-cased/unbound env override
        // leaves behind. The guard must reject booting half-configured.
        Assert.Throws<InvalidOperationException>(
            () => StartupValidation.ValidateRequiredConfig(new StartUpConfiguration()));
    }
}
