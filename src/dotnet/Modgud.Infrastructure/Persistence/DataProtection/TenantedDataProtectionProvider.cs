using System.Collections.Concurrent;
using Modgud.Infrastructure.Persistence.Tenancy;
using Microsoft.AspNetCore.DataProtection;

namespace Modgud.Infrastructure.Persistence.DataProtection;

/// <summary>
/// <see cref="IDataProtectionProvider"/> that hands every <c>Protect</c>
/// and <c>Unprotect</c> call off to the correct per-tenant inner provider.
/// Tenant is resolved from <see cref="TenantContext.Current"/> at the
/// point of the cryptographic operation — not at <c>CreateProtector</c>
/// time — so a single <see cref="IDataProtector"/> reference can be
/// reused across requests and still emit/consume tenant-correct payloads.
///
/// <para>Each per-tenant inner provider has its own
/// <see cref="MartenXmlRepository"/> bound to that tenant's database.
/// Tenant-A's keys never live in tenant-B's database, and a master-DB
/// compromise yields no cookie-forgery capability for any tenant.</para>
///
/// <para>Inner providers are cached after first creation (one mini
/// <see cref="IServiceProvider"/> per tenant). The cache size is bounded
/// by the realm count, so this isn't a memory concern.</para>
/// </summary>
public sealed class TenantedDataProtectionProvider : IDataProtectionProvider
{
    private readonly ConcurrentDictionary<string, IDataProtectionProvider> _byTenant = new();
    private readonly Func<string, IDataProtectionProvider> _factory;

    public TenantedDataProtectionProvider(Func<string, IDataProtectionProvider> factory)
    {
        _factory = factory;
    }

    public IDataProtector CreateProtector(string purpose)
    {
        ArgumentException.ThrowIfNullOrEmpty(purpose);
        return new TenantedDataProtector(this, new[] { purpose });
    }

    internal IDataProtectionProvider GetTenantProvider(string tenantId)
        => _byTenant.GetOrAdd(tenantId, _factory);
}

/// <summary>
/// Wrapper protector that resolves the active tenant from
/// <see cref="TenantContext.Current"/> on every Protect/Unprotect call.
/// Carries the framework-supplied purpose chain through nested
/// <c>CreateProtector</c> calls so the inner per-tenant protector ends
/// up with the same purpose stack a non-tenanted protector would have.
/// </summary>
public sealed class TenantedDataProtector : IDataProtector
{
    private readonly TenantedDataProtectionProvider _root;
    private readonly string[] _purposes;

    public TenantedDataProtector(TenantedDataProtectionProvider root, string[] purposes)
    {
        _root = root;
        _purposes = purposes;
    }

    public IDataProtector CreateProtector(string purpose)
    {
        ArgumentException.ThrowIfNullOrEmpty(purpose);
        var extended = new string[_purposes.Length + 1];
        Array.Copy(_purposes, extended, _purposes.Length);
        extended[_purposes.Length] = purpose;
        return new TenantedDataProtector(_root, extended);
    }

    public byte[] Protect(byte[] plaintext) => Resolve().Protect(plaintext);

    public byte[] Unprotect(byte[] protectedData) => Resolve().Unprotect(protectedData);

    private IDataProtector Resolve()
    {
        var tenant = TenantContext.Current;
        var provider = _root.GetTenantProvider(tenant);
        IDataProtector protector = provider.CreateProtector(_purposes[0]);
        for (var i = 1; i < _purposes.Length; i++)
            protector = protector.CreateProtector(_purposes[i]);
        return protector;
    }
}
