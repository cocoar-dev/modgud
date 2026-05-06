using ErrorOr;

namespace Cocoar.Auth.Authorization.Membership;

/// <summary>
/// Hard caps on admin-authored TypeScript / JavaScript script inputs that
/// flow into the JsEval pipeline. Two orthogonal limits — both run before
/// the TS compiler / parser ever sees the input:
///
/// <list type="bullet">
///   <item><b>Length</b> (<see cref="MaxScriptCharacters"/>) — closes Gap-2
///     from the JsEval threat model. An unbounded input lets the TS
///     compiler do arbitrary-time work even before the script runs.</item>
///   <item><b>Nesting depth</b> (<see cref="MaxNestingDepth"/>) — closes the
///     Cocoar.Auth-side window of F6b (lib parser stack-overflow on
///     deeply-nested expressions). Matters specifically because Tenant-
///     Admins author membership scripts: a 500-deep ternary in a script
///     would crash the host process on every recompute, taking down the
///     whole IdP, not just the script's own tenant. Until the lib gains a
///     parser-depth-counter, we count unmatched paren / brace / bracket
///     depth in the source string and reject anything over the threshold.</item>
/// </list>
///
/// <para>The two caps cover <c>Group.MembershipScript</c> and
/// <c>LoginProvider.UserUpdateScript</c>. Numbers are conservative:</para>
/// <list type="bullet">
///   <item>16 KiB is well above any realistic predicate or claim-mapping
///     script (existing seed scripts are under 1 KiB).</item>
///   <item>Depth 50 is well above any realistic predicate (typical depth
///     is &lt; 10) but well below the ~300-500 range where the
///     interpreted TS-parser-on-Jint exhausts the .NET stack.</item>
/// </list>
/// </summary>
public static class ScriptInputLimits
{
    /// <summary>
    /// Maximum allowed source-text size for a single user-authored script.
    /// 16 KiB; characters are counted (one UTF-16 char = one position),
    /// not bytes.
    /// </summary>
    public const int MaxScriptCharacters = 16 * 1024;

    /// <summary>
    /// Maximum allowed unmatched-paren/brace/bracket nesting depth in the
    /// source. 50 is comfortably above realistic predicates and well below
    /// the ~300 threshold where Jint's interpreted TS-parser starts
    /// consuming enough .NET stack to risk StackOverflow.
    /// </summary>
    public const int MaxNestingDepth = 50;

    /// <summary>
    /// Run both caps. Empty / null inputs pass — the call site's
    /// "is required" check handles those.
    /// </summary>
    /// <param name="script">The TS / JS source text.</param>
    /// <param name="errorCodePrefix">
    /// The prefix the call site uses for its domain — e.g.
    /// <c>"Group.MembershipScript"</c>. The helper appends
    /// <c>"TooLong"</c> or <c>"TooDeep"</c>.
    /// </param>
    public static Error? Validate(string? script, string errorCodePrefix)
    {
        if (string.IsNullOrEmpty(script)) return null;

        if (script.Length > MaxScriptCharacters)
        {
            return Error.Validation(
                errorCodePrefix + "TooLong",
                $"Script source must not exceed {MaxScriptCharacters:N0} characters " +
                $"(got {script.Length:N0}).");
        }

        var depth = MeasureMaxNestingDepth(script);
        if (depth > MaxNestingDepth)
        {
            return Error.Validation(
                errorCodePrefix + "TooDeep",
                $"Script nesting depth ({depth}) exceeds the limit ({MaxNestingDepth}). " +
                $"Refactor with intermediate variables or shorter chains.");
        }

        return null;
    }

    /// <summary>
    /// Walk the source counting open-vs-close brackets to find the
    /// maximum nesting depth. Skips:
    /// <list type="bullet">
    ///   <item>String literals (single, double, backtick) — the contents
    ///     might contain unmatched brackets that shouldn't count.</item>
    ///   <item>Line comments and block comments.</item>
    ///   <item>Template-string interpolation `${ … }` — interpolation
    ///     contents are real code and DO count, but the surrounding
    ///     backticks don't.</item>
    /// </list>
    /// Imperfect (e.g. doesn't track regex literals as their own state),
    /// but errs on the side of counting more depth than the parser would —
    /// safe direction for this defensive check.
    /// </summary>
    public static int MeasureMaxNestingDepth(string source)
    {
        var depth = 0;
        var maxDepth = 0;

        var inLineComment = false;
        var inBlockComment = false;
        var stringDelim = '\0'; // '\0' = not in string; otherwise the delimiter

        for (var i = 0; i < source.Length; i++)
        {
            var c = source[i];
            var next = i + 1 < source.Length ? source[i + 1] : '\0';

            // End-of-line ends a line comment.
            if (inLineComment)
            {
                if (c == '\n') inLineComment = false;
                continue;
            }

            // End of a block comment.
            if (inBlockComment)
            {
                if (c == '*' && next == '/') { inBlockComment = false; i++; }
                continue;
            }

            // Inside a string literal — skip until matching delimiter,
            // honouring backslash escapes.
            if (stringDelim != '\0')
            {
                if (c == '\\') { i++; continue; }   // skip the next char
                if (c == stringDelim) stringDelim = '\0';
                continue;
            }

            // Comment starts?
            if (c == '/' && next == '/') { inLineComment = true; i++; continue; }
            if (c == '/' && next == '*') { inBlockComment = true; i++; continue; }

            // String starts?
            if (c is '\'' or '"' or '`') { stringDelim = c; continue; }

            // Brackets.
            if (c is '(' or '[' or '{')
            {
                depth++;
                if (depth > maxDepth) maxDepth = depth;
            }
            else if (c is ')' or ']' or '}')
            {
                if (depth > 0) depth--;
                // Unmatched closes are a syntax error the parser will
                // catch; we don't care to flag them here.
            }
        }

        return maxDepth;
    }
}
