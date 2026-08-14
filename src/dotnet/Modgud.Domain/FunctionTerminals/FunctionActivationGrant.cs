namespace Modgud.Domain.FunctionTerminals;

/// <summary>
/// The right of ONE user to staff ONE function on its shared terminals
/// (MG-FT-02, plan §4.2). Version 1 deliberately has no terminal, location, or
/// time-window scoping — a grant covers every terminal of the function. A grant
/// never makes the user the business actor of later actions (the function is);
/// it only authorizes the passkey tap that opens a staffing session.
///
/// <para>Event-sourced: this document is the inline projection of a grant
/// stream (<c>FunctionActivationGrantProjection</c>), never written directly.
/// Who issued/suspended/revoked and when lives in the events; the document
/// carries the queryable snapshot.</para>
/// </summary>
public sealed class FunctionActivationGrant
{
    public Guid Id { get; set; }
    public Guid FunctionPrincipalId { get; set; }
    public Guid UserId { get; set; }

    public FunctionActivationGrantStatus Status { get; set; }
        = FunctionActivationGrantStatus.Active;

    public DateTimeOffset CreatedAt { get; set; }
    public Guid CreatedByUserId { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }
    public Guid? RevokedByUserId { get; set; }
}

public enum FunctionActivationGrantStatus
{
    Active,
    Suspended,
    Revoked,
}
