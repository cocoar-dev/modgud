using System.Text.Json;
using Jint;
using Jint.Native;
using Jint.Runtime;
using Microsoft.Extensions.Logging;

namespace Modgud.Authentication.Identity.ExternalAuth;

/// <summary>
/// Runs an IdP-specific user-update script against raw OIDC claims and produces
/// a patch descriptor that the login flow applies to the Modgud user record.
/// <para>
/// Script signature: <c>(claims) =&gt; ({ firstname, lastname, email, acronym })</c>.
/// Fields that are <c>undefined</c> (or missing) mean "do not touch"; explicit
/// <c>null</c> means "clear the value". No other properties are writable — the
/// runner silently ignores unknown keys.
/// </para>
/// <para>
/// Each invocation gets a fresh Jint engine (engines aren't thread-safe; setup
/// is cheap). Evaluation is wall-clock-capped to prevent a runaway script from
/// blocking a login; on any failure the runner returns an empty patch with
/// <c>Succeeded = false</c> and the error message, so the caller can decide
/// whether to still proceed with the login (it does) and log the failure to
/// the auth log.
/// </para>
/// </summary>
public class UserUpdateScriptRunner
{
    private static readonly TimeSpan ScriptTimeout = TimeSpan.FromMilliseconds(500);

    private readonly ILogger<UserUpdateScriptRunner> _logger;

    public UserUpdateScriptRunner(ILogger<UserUpdateScriptRunner> logger)
    {
        _logger = logger;
    }

    /// <param name="rawClaims">
    /// Claim → value(s) dictionary: each value is a string, number, bool, or a
    /// list of those. OpenID Connect multi-valued claims like <c>groups</c> come
    /// as string arrays.
    /// </param>
    public UserUpdateResult Run(string script, IReadOnlyDictionary<string, object?> rawClaims)
    {
        if (string.IsNullOrWhiteSpace(script))
        {
            _logger.LogWarning("Auth: UserUpdateScript called with empty script — no patch produced");
            return UserUpdateResult.Failed("script is empty");
        }

        try
        {
            var engine = new Engine(options => options
                .TimeoutInterval(ScriptTimeout)
                .LimitRecursion(32)
                .MaxStatements(5_000));

            var claimsAsDict = rawClaims.ToDictionary(kv => kv.Key, kv => kv.Value);

            var fn = engine.Evaluate(script);
            if (fn.IsUndefined() || fn.IsNull())
                return UserUpdateResult.Failed("script did not return a function");

            var result = engine.Invoke(fn, claimsAsDict);
            return MapToPatch(result);
        }
        catch (JavaScriptException jsEx)
        {
            _logger.LogWarning(jsEx, "Auth: UserUpdateScript error: {Message}", jsEx.Message);
            return UserUpdateResult.Failed(jsEx.Message);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Auth: UserUpdateScript timed out after {Ms}ms", ScriptTimeout.TotalMilliseconds);
            return UserUpdateResult.Failed("script timed out");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Auth: UserUpdateScript unexpected error");
            return UserUpdateResult.Failed(ex.Message);
        }
    }

    private static UserUpdateResult MapToPatch(JsValue result)
    {
        if (result.IsUndefined() || result.IsNull() || !result.IsObject())
            return UserUpdateResult.Failed("script did not return an object");

        var raw = result.ToObject() as IDictionary<string, object?> ?? new Dictionary<string, object?>();

        // Capture the raw script output as JSON for the debugging modal, BEFORE
        // we extract the recognized fields. That way the admin can see exactly
        // what the script emitted — including keys the runner silently ignored.
        JsonDocument? scriptOutput = SerializeSafely(raw);

        return new UserUpdateResult(
            Succeeded: true,
            Error: null,
            Firstname: ReadField(raw, "firstname"),
            Lastname: ReadField(raw, "lastname"),
            Email: ReadField(raw, "email"),
            Acronym: ReadField(raw, "acronym"),
            ScriptOutput: scriptOutput);
    }

    /// <summary>
    /// Reads a single-string-or-null field from the script output.
    /// <list type="bullet">
    ///   <item><c>FieldPresence.NotSet</c> — key missing or value is <c>undefined</c>.</item>
    ///   <item><c>FieldPresence.Null</c> — key present, value is explicit <c>null</c>.</item>
    ///   <item><c>FieldPresence.Value</c> — trimmed non-empty string.</item>
    /// </list>
    /// Empty strings collapse to <c>NotSet</c> — empty from a script is almost
    /// always an accident (concat with missing parts), and "clear" should be
    /// explicit via <c>null</c>.
    /// </summary>
    private static FieldPatch ReadField(IDictionary<string, object?> dict, string key)
    {
        if (!dict.TryGetValue(key, out var value))
            return FieldPatch.NotSet;
        if (value is null)
            return FieldPatch.Clear;

        var str = value.ToString();
        if (string.IsNullOrWhiteSpace(str))
            return FieldPatch.NotSet;

        return FieldPatch.SetTo(str);
    }

    private static JsonDocument? SerializeSafely(object value)
    {
        try
        {
            return JsonSerializer.SerializeToDocument(value);
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// Outcome of a user-update script run. <see cref="Succeeded"/> reflects whether
/// the script executed cleanly and returned a usable object. On failure, the
/// four field patches are all <see cref="FieldPresence.NotSet"/> and
/// <see cref="Error"/> carries the diagnostic.
/// </summary>
public record UserUpdateResult(
    bool Succeeded,
    string? Error,
    FieldPatch Firstname,
    FieldPatch Lastname,
    FieldPatch Email,
    FieldPatch Acronym,
    JsonDocument? ScriptOutput)
{
    public static UserUpdateResult Failed(string error) => new(
        Succeeded: false,
        Error: error,
        Firstname: FieldPatch.NotSet,
        Lastname: FieldPatch.NotSet,
        Email: FieldPatch.NotSet,
        Acronym: FieldPatch.NotSet,
        ScriptOutput: null);
}

/// <summary>
/// Three-state field patch produced by the script. Distinguishes "not mentioned"
/// (leave untouched) from "explicit null" (clear) and from a real value.
/// </summary>
public readonly record struct FieldPatch(FieldPresence Presence, string? Value)
{
    public static readonly FieldPatch NotSet = new(FieldPresence.NotSet, null);
    public static readonly FieldPatch Clear = new(FieldPresence.Null, null);
    public static FieldPatch SetTo(string value) => new(FieldPresence.Value, value);

    public bool IsSet => Presence != FieldPresence.NotSet;
}

public enum FieldPresence
{
    NotSet,  // key missing / undefined / empty → don't touch
    Null,    // explicit null → clear the value
    Value,   // concrete non-empty string
}
