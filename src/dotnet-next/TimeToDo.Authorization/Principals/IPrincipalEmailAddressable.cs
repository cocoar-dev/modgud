namespace TimeToDo.Authorization.Principals;

/// <summary>
/// A principal that can receive email. The concrete class decides the resolution
/// semantics — a person returns their own address, a shared-mailbox group returns
/// the shared address, an expand-to-members group recursively collects its
/// members' addresses.
/// </summary>
public interface IPrincipalEmailAddressable : IPrincipal
{
    /// <summary>
    /// Returns every deliverable address for this principal. Empty list when
    /// the principal has no addressable email (yet). Async so containers can
    /// expand via the supplied <paramref name="context"/> without holding a
    /// document-session reference on the principal itself.
    /// </summary>
    Task<IReadOnlyList<string>> GetEmailsAsync(IEmailResolutionContext context, CancellationToken ct = default);
}

/// <summary>
/// Injected into <see cref="IPrincipalEmailAddressable.GetEmailsAsync"/> so expand-to-members
/// resolution can look up nested principals without the principal holding a
/// persistence reference itself. Implementations must be side-effect-free lookups.
/// </summary>
public interface IEmailResolutionContext
{
    Task<IPrincipal?> LoadPrincipalAsync(Guid id, CancellationToken ct = default);
}
