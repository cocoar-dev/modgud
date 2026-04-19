namespace Cocoar.Auth.Domain.Principals;

/// <summary>
/// A principal that can authenticate (log in). Persons use cookie/MFA/passkey;
/// service principals use API keys or tokens. Both satisfy this marker so
/// common login/logout/session code can operate on either.
/// </summary>
public interface IAuthenticatable : IPrincipal
{
}
