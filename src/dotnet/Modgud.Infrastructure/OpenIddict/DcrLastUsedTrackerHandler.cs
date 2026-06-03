using System.Text.Json;
using Modgud.Domain.OAuth.Applications;
using Modgud.Infrastructure.Audit;
using Marten;
using OpenIddict.Server;

namespace Modgud.Infrastructure.OpenIddict;

/// <summary>
/// Updates <c>cocoar:dcr:last_used_at</c> on a DCR client's
/// Application-properties dict on every successful sign-in. The
/// timestamp drives the GC sweep — clients with
/// <c>LastUsedAt &lt; (now - TTL)</c> get soft-deleted by
/// <c>DcrGarbageCollectorService</c>.
///
/// <para>The first time the value diverges from the original
/// <c>RegisteredAt</c> stamp, the handler also logs a
/// <c>DCR client first used</c> audit event — clean signal for "the
/// registration was real, not bot noise."</para>
///
/// <para>Runs after <see cref="DcrAudienceContainmentHandler"/> so the
/// containment check has had a chance to <c>Reject(...)</c> first;
/// rejected sign-ins don't bump LastUsedAt. One additional Marten
/// write per successful DCR-token issue (load aggregate + append event
/// + save); admin-created clients pay nothing.</para>
/// </summary>
public sealed class DcrLastUsedTrackerHandler
    : IOpenIddictServerHandler<OpenIddictServerEvents.ProcessSignInContext>
{
    public static OpenIddictServerHandlerDescriptor Descriptor { get; }
        = OpenIddictServerHandlerDescriptor.CreateBuilder<OpenIddictServerEvents.ProcessSignInContext>()
            .UseScopedHandler<DcrLastUsedTrackerHandler>()
            .SetOrder(DcrAudienceContainmentHandler.Descriptor.Order + 1)
            .SetType(OpenIddictServerHandlerType.Custom)
            .Build();

    private readonly IDocumentSession _session;
    private readonly ISecurityAuditLog _securityAudit;

    public DcrLastUsedTrackerHandler(IDocumentSession session, ISecurityAuditLog securityAudit)
    {
        _session = session;
        _securityAudit = securityAudit;
    }

    public async ValueTask HandleAsync(OpenIddictServerEvents.ProcessSignInContext context)
    {
        // Upstream may have rejected (e.g. DcrAudienceContainmentHandler);
        // skip if the pipeline already short-circuited.
        if (context.IsRejected) return;
        if (context.Principal is null) return;

        var clientId = context.Request.ClientId;
        if (string.IsNullOrEmpty(clientId)) return;

        var state = await _session.Query<OAuthApplicationState>()
            .FirstOrDefaultAsync(x => x.ClientId == clientId && !x.IsDeleted);
        if (state is null) return;

        var props = state.Properties;
        if (!GetBool(props, OAuthApplicationPropertyKeys.DcrIsDynamicallyRegistered)) return;

        var aggregate = await _session.Events.AggregateStreamAsync<OAuthApplicationAggregate>(state.Id);
        if (aggregate is null || aggregate.IsDeleted) return;

        var now = DateTimeOffset.UtcNow;
        var registeredAt = GetString(props, OAuthApplicationPropertyKeys.DcrRegisteredAt);
        var lastUsedAtBefore = GetString(props, OAuthApplicationPropertyKeys.DcrLastUsedAt);
        var isFirstUse = lastUsedAtBefore == registeredAt;

        var newProps = new Dictionary<string, object?>(aggregate.Properties)
        {
            [OAuthApplicationPropertyKeys.DcrLastUsedAt] = JsonSerializer.SerializeToElement(now.ToString("O")),
        };

        _session.Events.Append(state.Id, aggregate.SetProperties(newProps));
        await _session.SaveChangesAsync();

        if (isFirstUse)
        {
            _securityAudit.Record(new SecurityAuditRecord
            {
                EventType = AuditEvents.DcrClientFirstUsed,
                Level = "Info",
                Actor = clientId,
                Status = "first_used",
                Reason = $"registeredAt {registeredAt ?? "(unknown)"}",
                Message = $"DCR client {clientId} used for the first time",
            });
        }
    }

    private static bool GetBool(IDictionary<string, object?> props, string key)
    {
        if (!props.TryGetValue(key, out var raw) || raw is null) return false;
        return raw switch
        {
            bool b => b,
            JsonElement e when e.ValueKind is JsonValueKind.True => true,
            JsonElement e when e.ValueKind is JsonValueKind.False => false,
            _ => false,
        };
    }

    private static string? GetString(IDictionary<string, object?> props, string key)
    {
        if (!props.TryGetValue(key, out var raw) || raw is null) return null;
        return raw switch
        {
            string s => s,
            JsonElement e when e.ValueKind is JsonValueKind.String => e.GetString(),
            _ => null,
        };
    }
}
