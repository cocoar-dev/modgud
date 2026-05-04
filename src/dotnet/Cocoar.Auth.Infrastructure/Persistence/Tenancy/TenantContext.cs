namespace Cocoar.Auth.Infrastructure.Persistence.Tenancy;

/// <summary>
/// AsyncLocal-backed ambient tenant identifier. Survives <c>CreateScope()</c>
/// boundaries that <c>HttpContextAccessor</c>-only chains do not — solves the
/// class of bug where a scoped service spins up an inner DI scope to grab a
/// fresh <c>IMessageBus</c>, which then has <c>TenantId == null</c> because
/// the bus instance is brand new (see WOLV-01: DemoSeedService phase 7
/// crashing in <c>Marten.MasterTableTenancy.get_Default()</c>).
///
/// <para>
/// Population strategy:
/// </para>
/// <list type="bullet">
///   <item><description><c>RealmMiddleware</c> sets <see cref="Set"/> at request entry.</description></item>
///   <item><description>Background / hosted services enter explicitly via <see cref="Enter"/>.</description></item>
///   <item><description>Wolverine bus-dispatch propagates <see cref="Current"/> onto the message envelope.</description></item>
///   <item><description><c>TenantedSessionFactory</c> falls back to <see cref="Current"/> if <c>HttpContext.Items</c> is empty.</description></item>
/// </list>
///
/// <para>
/// Reading <see cref="Current"/> with no active scope returns
/// <see cref="TenantConstants.SystemTenantId"/> — the same fallback policy
/// <c>TenantedSessionFactory</c> already uses for HttpContext-less paths.
/// </para>
/// </summary>
public static class TenantContext
{
    private static readonly AsyncLocal<string?> _current = new();

    /// <summary>
    /// Currently active tenant slug, or <c>"system"</c> if no scope has set one.
    /// </summary>
    public static string Current => _current.Value ?? TenantConstants.SystemTenantId;

    /// <summary>
    /// Raw value — <see langword="null"/> when no scope has set a tenant.
    /// Distinguishes "no tenant context at all" from "system tenant explicitly
    /// chosen". Use this in places that need to know whether a fallback is
    /// happening; everything else should use <see cref="Current"/>.
    /// </summary>
    public static string? CurrentOrNull => _current.Value;

    /// <summary>
    /// Sets the tenant slug for the current async flow. Used by
    /// <c>RealmMiddleware</c> at request entry — once set, every downstream
    /// <c>await</c> sees the same value until the scope exits.
    /// </summary>
    public static void Set(string slug)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        _current.Value = slug;
    }

    /// <summary>
    /// Disposable scope that sets the tenant for its lifetime and restores
    /// the previous value on dispose. Use from background services, demo-
    /// seed import, integration tests — any path where there's no
    /// <c>RealmMiddleware</c> to set the tenant for us.
    /// </summary>
    /// <example>
    /// <code>
    /// using var _ = TenantContext.Enter("system");
    /// await bus.InvokeAsync(command);
    /// </code>
    /// </example>
    public static IDisposable Enter(string slug)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        var previous = _current.Value;
        _current.Value = slug;
        return new RestoreScope(previous);
    }

    private sealed class RestoreScope : IDisposable
    {
        private readonly string? _previous;
        private bool _disposed;

        public RestoreScope(string? previous) => _previous = previous;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _current.Value = _previous;
        }
    }
}
