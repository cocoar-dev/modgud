namespace Modgud.Authorization.Services;

/// <summary>
/// Resolves a principal id to the flat list of email addresses it represents as
/// a notification target. Concrete email semantics (person's own address, group
/// shared mailbox, expand-to-members traversal) live on the principal types
/// themselves via <c>IPrincipalEmailAddressable.GetEmailsAsync</c>; this service
/// is just the persistence-aware wrapper.
/// </summary>
public interface IPrincipalEmailResolver
{
    Task<IReadOnlyList<string>> ResolveEmailsAsync(Guid principalId, CancellationToken ct = default);
}
