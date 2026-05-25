using Modgud.Infrastructure.Persistence.Tenancy;

namespace Modgud.Tests.Unit.Infrastructure.Persistence.Tenancy;

/// <summary>
/// Pin <see cref="TenantConstants"/> values. These constants are wire-level
/// contracts: <c>SystemTenantId</c> is what every background service falls
/// back to, and the HttpContext.Items keys are read by middleware all over
/// the request pipeline. Renaming them silently breaks tenant isolation.
/// </summary>
public class TenantConstantsTests
{
    [Fact]
    public void SystemTenantId_is_system()
    {
        Assert.Equal("system", TenantConstants.SystemTenantId);
    }

    [Fact]
    public void HttpContextTenantIdKey_is_TenantId()
    {
        Assert.Equal("TenantId", TenantConstants.HttpContextTenantIdKey);
    }

    [Fact]
    public void HttpContextTenantInfoKey_is_TenantInfo()
    {
        Assert.Equal("TenantInfo", TenantConstants.HttpContextTenantInfoKey);
    }
}
