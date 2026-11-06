namespace Modgud.Infrastructure.FollowUps;

/// <summary>
/// A follow-up effect of a state change that is already decided — revoke a grant, send a mail,
/// create inbox items. It is published through <see cref="IFollowUpOutbox"/> on the SAME
/// Marten session as the state change, so both commit together or not at all, and it is then
/// handled from the durable <see cref="FollowUpConventions.QueueName"/> queue with retries.
/// <para>A handler of a follow-up must be idempotent: Wolverine delivers at least once.</para>
/// </summary>
public interface IFollowUpMessage;
