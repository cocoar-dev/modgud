using Cocoar.Auth.Authorization.Principals;
using Marten;

namespace Cocoar.Auth.Authorization.Services;

/// <summary>
/// Default email resolver. Loads the principal, delegates the actual email
/// production to its <see cref="IPrincipalEmailAddressable.GetEmailsAsync"/>
/// implementation, then de-dupes the results.
/// </summary>
public class PrincipalEmailResolver(IQuerySession session) : IPrincipalEmailResolver
{
    public async Task<IReadOnlyList<string>> ResolveEmailsAsync(Guid principalId, CancellationToken ct = default)
    {
        var principal = await session.LoadAsync<Principal>(principalId, ct);
        if (principal is null || principal.IsDeleted || !principal.IsActive) return [];

        if (principal is not IPrincipalEmailAddressable addressable) return [];

        var context = new ResolutionContext(session);
        var raw = await addressable.GetEmailsAsync(context, ct);

        // De-dupe case-insensitively; two groups pointing at the same shared
        // mailbox should result in one send, not two.
        return raw
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private sealed class ResolutionContext(IQuerySession session) : IEmailResolutionContext
    {
        public async Task<IPrincipal?> LoadPrincipalAsync(Guid id, CancellationToken ct = default)
            => await session.LoadAsync<Principal>(id, ct);
    }
}
