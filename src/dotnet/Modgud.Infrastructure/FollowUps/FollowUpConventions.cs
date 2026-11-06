using JasperFx;
using JasperFx.CodeGeneration;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;
using Wolverine.Runtime.Handlers;

namespace Modgud.Infrastructure.FollowUps;

/// <summary>
/// Routing and failure handling of <see cref="IFollowUpMessage"/>s: one durable local queue
/// (persisted in the realm's database, recovered after a crash) and a retry policy that first
/// retries in place, then reschedules, and finally parks the message as a dead letter.
/// </summary>
public static class FollowUpConventions
{
    public const string QueueName = "follow-ups";

    /// <summary>Retries in place, for a short hiccup (a deadlock, a busy SMTP server).</summary>
    public static readonly TimeSpan[] Cooldowns = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15)];

    /// <summary>Rescheduled retries, for an outage; afterwards the message is a dead letter.</summary>
    public static readonly TimeSpan[] Reschedules = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(10), TimeSpan.FromHours(1), TimeSpan.FromHours(6)];

    public static void AddFollowUps(this WolverineOptions opts)
    {
        opts.Publish(publish =>
        {
            publish.MessagesImplementing<IFollowUpMessage>();
            publish.ToLocalQueue(QueueName);
        });
        opts.LocalQueue(QueueName).UseDurableInbox();
        opts.Policies.Add(new FollowUpFailurePolicy());
    }

    public static IServiceCollection AddFollowUpOutbox(this IServiceCollection services) =>
        services.AddScoped<IFollowUpOutbox, FollowUpOutbox>();

    private sealed class FollowUpFailurePolicy : IHandlerPolicy
    {
        public void Apply(IReadOnlyList<HandlerChain> chains, GenerationRules rules, IServiceContainer container)
        {
            foreach (var chain in chains.Where(c => typeof(IFollowUpMessage).IsAssignableFrom(c.MessageType)))
            {
                chain.OnAnyException()
                    .RetryWithCooldown(Cooldowns)
                    .Then.ScheduleRetry(Reschedules)
                    .Then.MoveToErrorQueue();
            }
        }
    }
}
