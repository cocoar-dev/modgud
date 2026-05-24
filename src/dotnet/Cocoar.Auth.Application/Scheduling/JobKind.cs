namespace Cocoar.Auth.Application.Scheduling;

/// <summary>
/// Discriminator for scheduled jobs. <see cref="System"/> jobs are compiled
/// .NET handlers shipped with the app (e.g. DcrGcJob, JobRunHistoryRetentionJob).
/// <see cref="Script"/> is reserved for future JsEval-authored jobs — not
/// exercised yet, kept on the model so the storage shape doesn't change later.
/// </summary>
public enum JobKind
{
    System = 0,
    Script = 1,
}
