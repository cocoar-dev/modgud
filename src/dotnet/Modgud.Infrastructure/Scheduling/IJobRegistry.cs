namespace Modgud.Infrastructure.Scheduling;

/// <summary>
/// Startup-time catalogue of all known system jobs. Each registration ships
/// with a default cron — admin can override via the Jobs page in the UI,
/// stored as a <see cref="JobConfig"/> Marten document and applied on the
/// next startup or via a live reschedule.
/// </summary>
public interface IJobRegistry
{
    /// <summary>Register a compiled job type with default schedule.</summary>
    void Register(JobRegistration registration);

    /// <summary>Read-only view of everything that's been registered.</summary>
    IReadOnlyCollection<JobRegistration> All { get; }
}

public sealed class JobRegistry : IJobRegistry
{
    private readonly Dictionary<string, JobRegistration> _byKey = new(StringComparer.OrdinalIgnoreCase);

    public JobRegistry(IEnumerable<JobRegistration> registrations)
    {
        foreach (var r in registrations) Register(r);
    }

    public void Register(JobRegistration registration)
    {
        if (_byKey.ContainsKey(registration.Key))
            throw new InvalidOperationException($"Job key '{registration.Key}' is already registered");
        _byKey[registration.Key] = registration;
    }

    public IReadOnlyCollection<JobRegistration> All => _byKey.Values;
}
