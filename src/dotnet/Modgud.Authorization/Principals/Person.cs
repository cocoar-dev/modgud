namespace Modgud.Authorization.Principals;

/// <summary>
/// Natural-person principal — the human side of the principal hierarchy.
/// Concrete + sealed for Modgud's needs: identity fields (name + acronym)
/// for display and search, an account name for login lookup, an email for
/// notifications, and a list of linked external identities (IdP-managed
/// logins). Stored polymorphically with <see cref="Group"/> in the
/// <c>mt_doc_principal</c> table via Marten's sub-class mapping.
/// </summary>
public class Person : Principal, IPrincipalWithAccount, IPrincipalEmailAddressable
{
    public override string Type => "person";

    public string? AccountName { get; set; }
    public string? Firstname { get; set; }
    public string? Lastname { get; set; }
    public string? Acronym { get; set; }
    public string? Email { get; set; }
    public string? NormalizedUserName { get; set; }
    public string? NormalizedEmail { get; set; }

    /// <summary>
    /// Links to external identity providers (Google, Entra, etc.) that can
    /// authenticate as this person. Maintained by the IdP-integration flow.
    /// </summary>
    public List<ExternalIdentityRef> ExternalIdentities { get; set; } = [];

    public override string DisplayName
    {
        get
        {
            var parts = new[] { Acronym, $"{Firstname ?? ""} {Lastname ?? ""}".Trim() }
                .Where(p => !string.IsNullOrWhiteSpace(p));
            var label = string.Join(" | ", parts);
            return !string.IsNullOrWhiteSpace(label) ? label : AccountName ?? Id.ToString();
        }
    }

    public Task<IReadOnlyList<string>> GetEmailsAsync(
        IEmailResolutionContext context,
        CancellationToken ct = default)
    {
        IReadOnlyList<string> result = string.IsNullOrWhiteSpace(Email) ? [] : [Email!];
        return Task.FromResult(result);
    }
}

/// <summary>
/// Thin reference from a person to one of their external identity links. Carries
/// only stable identifiers — name/email snapshots live on the link aggregate
/// itself (<c>ExternalIdentityLink</c> in Modgud's IdP feature).
/// </summary>
public record ExternalIdentityRef(
    Guid LinkId,
    Guid LoginProviderId,
    string Issuer);
