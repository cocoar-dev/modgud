namespace Cocoar.Auth.Authorization.Principals;

/// <summary>
/// A principal that carries an account identifier — e.g. a person's username,
/// a service account's client id, a bot's handle. Used wherever code needs a
/// short, human-typable identity pointer (login lookups, audit logs, API
/// authentication).
/// </summary>
public interface IPrincipalWithAccount : IPrincipal
{
    string? AccountName { get; }
}
