namespace Modgud.Api.Startup;

/// <summary>
/// Fail-loud guards for required configuration. The cold-start ladder's single
/// invariant is "no silent failures": a required value left at its empty default
/// must surface a clear, actionable error at startup, not boot far and then fail
/// with a cryptic message far from the cause.
///
/// <para>The canonical trigger is an environment-variable override that didn't
/// bind (a separator or property-name mismatch — Cocoar.Configuration v6 binds
/// env overrides case-insensitively by <c>Section__Property</c>, so casing is
/// not the culprit): the property keeps its compiled-in default and the app
/// boots in a half-configured shape. For the DB connection string that default
/// is empty, which today only surfaces deep in the cold-boot DB-creation path
/// as <c>"...missing 'Database='"</c> — confusing and late.</para>
/// </summary>
public static class StartupValidation
{
    /// <summary>
    /// Validate the configuration the app cannot run without. Throws
    /// <see cref="InvalidOperationException"/> with an actionable message naming
    /// the offending key. Safe to call once, right after the configuration is
    /// resolved and before any service consumes it.
    /// </summary>
    public static void ValidateRequiredConfig(StartUpConfiguration conf)
    {
        ArgumentNullException.ThrowIfNull(conf);

        if (string.IsNullOrWhiteSpace(conf.DbSettings.ConnectionString))
            throw new InvalidOperationException(
                "DbSettings:ConnectionString is not configured. Modgud has no PostgreSQL " +
                "connection string and would otherwise fail deeper in the cold-boot path " +
                "with a cryptic database error. Set it in data/configuration.json (the " +
                "\"DbSettings\": { \"ConnectionString\": ... } key) or via the " +
                "'DbSettings__ConnectionString' environment variable — and if you set it " +
                "via the environment but it isn't picked up, re-check the variable's name " +
                "and the '__' section separator (binding is case-insensitive).");
    }
}
