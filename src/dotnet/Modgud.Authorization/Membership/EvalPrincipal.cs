using Modgud.Authorization.Principals;

namespace Modgud.Authorization.Membership;

/// <summary>
/// Federation v1 — the in-memory-only principal a login-time membership script
/// binds to (decision C/F). It is a transient <see cref="Person"/> with the
/// ephemeral, session-scoped external surface (<see cref="ExternalGroups"/> /
/// <see cref="Source"/>) overlaid; it exists only for the current login.
/// <para>
/// <b>It derives from <see cref="Person"/> on purpose.</b> Empirically,
/// <c>Type.Is(p, 'person')</c> compiles to a CLR type check against the
/// discriminator-registered type — a non-<c>Principal</c> wrapper returns
/// <c>false</c> and no person script ever matches. Deriving from <see cref="Person"/>
/// makes <c>Type.Is</c> narrow correctly and inherits the local fields so the
/// same script classifies identically on both engines (two-engine parity).
/// </para>
/// <para>
/// <b>NEVER persist this type.</b> It is deliberately NOT registered with Marten
/// (<c>AddSubClass</c>) or STJ (<c>JsonDerivedType</c>) — a <c>session.Store</c>
/// would land it in <c>mt_doc_evalprincipal</c> and corrupt the principal table
/// (the subclass double-registration trap). It is constructed only by
/// <c>LoginTimeMembershipDeriver</c> and evaluated only as the generic
/// <c>TPrincipal</c> of an in-memory compiled predicate over an
/// <c>IQuerySession</c> — the persisted JSONB-batch path never sees it.
/// </para>
/// </summary>
public sealed class EvalPrincipal : Person
{
    /// <summary>
    /// The current provider's <c>groups</c> claim values (source = local ∪
    /// provider:&lt;current&gt;), always an array so a script can do
    /// <c>p.ExternalGroups.includes('...')</c> regardless of count. This is the
    /// canonical federation membership signal ("is the user in upstream group X").
    /// </summary>
    public string[] ExternalGroups { get; init; } = [];

    /// <summary>Source tag of the current login (<c>"provider:&lt;slug&gt;"</c>).</summary>
    public string Source { get; init; } = "";
}
