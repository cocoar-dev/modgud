namespace Modgud.Infrastructure.Scheduling;

/// <summary>
/// Startup-time catalogue of all known compiled jobs. Each registration ships
/// with an ownership scope and default cron. Realm overrides are tenant-owned;
/// system overrides live in the global store and are controlled by the current
/// Control Plane.
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
