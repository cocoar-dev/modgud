using Modgud.Infrastructure.Persistence.Tenancy;
using Wolverine;

namespace Modgud.Infrastructure.FollowUps;

/// <summary>
/// Enters the realm a follow-up belongs to. <see cref="IFollowUpOutbox"/> stamps the realm of the
/// publishing session on the envelope; services the handler resolves (OpenIddict stores, the inbox,
/// the mail branding) open their sessions through the tenanted session factory, which needs it
/// as the ambient realm. A follow-up without a realm cannot be handled anywhere: dead letter.
/// </summary>
public static class FollowUpRealm
{
    public static IDisposable Enter(Envelope envelope, string what)
    {
        if (string.IsNullOrEmpty(envelope.TenantId))
            throw new FollowUpPermanentException($"{what} arrived without a realm.");
        return TenantContext.Enter(envelope.TenantId);
    }
}
