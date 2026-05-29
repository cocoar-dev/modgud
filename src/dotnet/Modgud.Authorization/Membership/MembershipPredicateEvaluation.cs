using Microsoft.Extensions.Logging;

namespace Modgud.Authorization.Membership;

/// <summary>
/// The single canonical in-memory membership evaluation: compile the predicate
/// and invoke it, treating ANY throw (notably a NullReferenceException from a
/// missing field, e.g. <c>p.Email.endsWith(...)</c> when Email is null) as
/// "not a member" — the safe default.
/// <para>
/// Both in-memory consumers — the durable per-principal recalculator and the
/// federation login-time deriver — call this so the swallow-semantics and the
/// two-engine parity surface live in ONE place, not two copies.
/// </para>
/// </summary>
internal static class MembershipPredicateEvaluation
{
    public static bool EvaluateSafe<TPrincipal>(
        IMembershipEvaluator evaluator,
        string compiledScript,
        TPrincipal principal,
        ILogger logger,
        Guid principalId,
        CancellationToken ct)
    {
        try
        {
            var compiled = evaluator.BuildPredicate<TPrincipal>(compiledScript, ct).Compile();
            return compiled(principal);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Membership predicate threw for principal {PrincipalId}", principalId);
            return false;
        }
    }
}
