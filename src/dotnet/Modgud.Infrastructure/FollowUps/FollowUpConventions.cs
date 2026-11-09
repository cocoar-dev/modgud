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
        // A handler that publishes follow-ups gets the request scope's outbox (one per session);
        // the implementation is internal, so Wolverine must not try to construct it itself.
        opts.CodeGeneration.AlwaysUseServiceLocationFor<IFollowUpOutbox>();
    }

    public static IServiceCollection AddFollowUpOutbox(this IServiceCollection services)
    {
        services.AddSingleton(new FollowUpOptions());
        return services.AddScoped<IFollowUpOutbox, FollowUpOutbox>();
    }

    private sealed class FollowUpFailurePolicy : IHandlerPolicy
    {
        public void Apply(IReadOnlyList<HandlerChain> chains, GenerationRules rules, IServiceContainer container)
        {
            foreach (var chain in chains.Where(c => typeof(IFollowUpMessage).IsAssignableFrom(c.MessageType)))
            {
                // First matching rule wins: a declared permanent failure skips the retries.
                chain.OnException<FollowUpPermanentException>().MoveToErrorQueue();
                chain.OnAnyException()
                    .RetryWithCooldown(Cooldowns)
                    .Then.ScheduleRetry(Reschedules)
                    .Then.MoveToErrorQueue();
            }
        }
    }
}

/// <summary>Timing of follow-ups.</summary>
public sealed class FollowUpOptions
{
    /// <summary>
    /// How long a safety net (<see cref="IFollowUpOutbox.PublishSafetyNetAsync{T}"/>) waits before
    /// it runs: longer than the immediate attempt it backs up takes, so the two never run at once
    /// on the same data (they did — a revoke and its follow-up appended to the same event stream
    /// and one failed on the stream version). Short enough that a failed attempt is completed soon.
    /// </summary>
    public TimeSpan SafetyNetDelay { get; set; } = TimeSpan.FromSeconds(10);
}
