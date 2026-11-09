using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Application.Inbox;
using Modgud.Infrastructure.Email;
using Modgud.Infrastructure.FollowUps;
using Wolverine;

namespace Modgud.Authentication.Notifications;

/// <summary>
/// B3-EI-04 — a templated mail owed by a state change, written in the commit of that change. The
/// model is complete (branding applied, values rendered) when it is published: the handler has no
/// request to resolve them from, and the state may have moved on. Retried on the follow-up schedule;
/// a mail is sent again only when its earlier attempt failed.
/// <para>Never put a secret (a verification token, a magic link) into this message: it is stored in
/// the realm's envelope tables until handled and in the dead letters when it fails for good.</para>
/// </summary>
public sealed record SendTemplatedEmail(string[] Recipients, EmailTemplate Template, Dictionary<string, string> Model) : IFollowUpMessage;

/// <summary>Inbox items owed by a state change (see <see cref="IInboxNotifier.NotifyAsync"/>).
/// <paramref name="ParametersJson"/> is rendered when published, like a mail model.</summary>
public sealed record CreateInboxItems(
    InboxKind Kind, Guid[] Recipients, string TitleKey, string? BodyKey, string? ParametersJson,
    string? Link, string? SourceType, Guid? SourceId) : IFollowUpMessage
{
    public static CreateInboxItems For(
        InboxKind kind, IEnumerable<Guid> recipients, string titleKey, string? bodyKey, object? parameters,
        string? link, string? sourceType, Guid? sourceId) =>
        new(kind, recipients.ToArray(), titleKey, bodyKey,
            parameters is null ? null : JsonSerializer.Serialize(parameters), link, sourceType, sourceId);
}

/// <summary>Inbox items a state change made obsolete: the known ids, and every open item of the
/// source (optionally of one kind), including items created after the ids were read.</summary>
public sealed record DismissInboxItems(Guid[] ItemIds, string? SourceType, Guid? SourceId, InboxKind? OnlyKind) : IFollowUpMessage;

public static class SendTemplatedEmailHandler
{
    public static async Task Handle(SendTemplatedEmail message, Envelope envelope, IServiceScopeFactory scopes, CancellationToken ct)
    {
        using var realm = FollowUpRealm.Enter(envelope, $"Mail {message.Template}");
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IEmailService>()
            .SendTemplatedEmailAsync(message.Recipients, message.Template, message.Model, ct);
    }
}

public static class CreateInboxItemsHandler
{
    public static async Task Handle(CreateInboxItems message, Envelope envelope, IServiceScopeFactory scopes, CancellationToken ct)
    {
        using var realm = FollowUpRealm.Enter(envelope, $"Inbox items {message.Kind}");
        await using var scope = scopes.CreateAsyncScope();
        using var parameters = message.ParametersJson is null ? null : JsonDocument.Parse(message.ParametersJson);
        await scope.ServiceProvider.GetRequiredService<IInboxNotifier>().NotifyAsync(
            message.Kind, message.Recipients, message.TitleKey, message.BodyKey, parameters?.RootElement,
            message.Link, message.SourceType, message.SourceId, ct);
    }
}

public static class DismissInboxItemsHandler
{
    public static async Task Handle(DismissInboxItems message, Envelope envelope, IServiceScopeFactory scopes, CancellationToken ct)
    {
        using var realm = FollowUpRealm.Enter(envelope, "Inbox dismissal");
        await using var scope = scopes.CreateAsyncScope();
        var inbox = scope.ServiceProvider.GetRequiredService<IInboxNotifier>();
        if (message.ItemIds.Length > 0)
            await inbox.DismissByIdsAsync(message.ItemIds, ct);
        if (message.SourceType is not null && message.SourceId is { } sourceId)
            await inbox.DismissBySourceAsync(message.SourceType, sourceId, message.OnlyKind, ct);
    }
}
