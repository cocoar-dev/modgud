using Modgud.Infrastructure.Persistence.Tenancy;

namespace Modgud.Tests.Unit.Infrastructure.Persistence.Tenancy;

/// <summary>
/// Pin <see cref="TenantConstants"/> values. These constants are wire-level
/// compatibility contracts and HttpContext.Items keys read by middleware.
/// Runtime tenant resolution deliberately has no implicit system fallback.
/// </summary>
public class TenantConstantsTests
{
    [Fact]
    public void SystemTenantId_is_system()
    {
        Assert.Equal("system", TenantConstants.SystemTenantId);
    }

    [Fact]
    public void TenantContext_without_an_explicit_realm_fails_closed()
    {
        Assert.Null(TenantContext.CurrentOrNull);
        var error = Assert.Throws<InvalidOperationException>(() => TenantContext.Current);
        Assert.Contains("No realm context", error.Message);
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
