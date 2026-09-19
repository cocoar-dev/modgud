using System.Text.Json;
using Modgud.Application.Dcr;
using Modgud.Domain.OAuth.Scopes;

namespace Modgud.Tests.Unit.OAuth.Dcr;

/// <summary>
/// Pins the one rule behind the CIMD permissions, the DCR registration and the
/// authorize-time check: which scopes a dynamic client may hold, and how a
/// declared <c>scope</c> is reconciled with that set.
/// </summary>
public class DynamicClientScopePolicyTests
{
    [Theory]
    [InlineData("openid")]
    [InlineData("profile")]
    [InlineData("email")]
    [InlineData("phone")]
    [InlineData("address")]
    [InlineData("offline_access")]
    [InlineData("roles")]
    [InlineData("permissions")]
    public void Every_standard_scope_but_the_management_selector_is_open(string name)
    {
        Assert.True(DynamicClientScopePolicy.IsOpenStandardScope(name));
        Assert.True(DynamicClientScopePolicy.IsRequestable(Scope(name)));
    }

    [Fact]
    public void The_management_selector_is_never_requestable_by_dynamic_clients()
    {
        Assert.False(DynamicClientScopePolicy.IsOpenStandardScope(StandardScopes.Management));
        Assert.False(DynamicClientScopePolicy.IsRequestable(Scope(StandardScopes.Management)));
        // Not even with the flag — the seeder strips it, the policy ignores it.
        Assert.False(DynamicClientScopePolicy.IsRequestable(Scope(StandardScopes.Management, optIn: true)));
    }

    [Fact]
    public void A_custom_scope_needs_the_opt_in_whether_app_scoped_or_global()
    {
        Assert.False(DynamicClientScopePolicy.IsRequestable(Scope("lists.read")));
        Assert.True(DynamicClientScopePolicy.IsRequestable(Scope("lists.read", optIn: true)));
        Assert.False(DynamicClientScopePolicy.IsRequestable(Scope("lists.read", appId: Guid.NewGuid())));
        Assert.True(DynamicClientScopePolicy.IsRequestable(Scope("lists.read", optIn: true, appId: Guid.NewGuid())));
    }

    [Fact]
    public void The_opt_in_reads_as_plain_bool_or_json_element()
    {
        var plain = Scope("a");
        plain.Properties[ScopePropertyKeys.AllowDynamicRegistrationClients] = true;
        Assert.True(DynamicClientScopePolicy.HasOptIn(plain.Properties));

        var element = Scope("b");
        element.Properties[ScopePropertyKeys.AllowDynamicRegistrationClients] = JsonSerializer.SerializeToElement(true);
        Assert.True(DynamicClientScopePolicy.HasOptIn(element.Properties));

        var off = Scope("c");
        off.Properties[ScopePropertyKeys.AllowDynamicRegistrationClients] = JsonSerializer.SerializeToElement(false);
        Assert.False(DynamicClientScopePolicy.HasOptIn(off.Properties));
        Assert.False(DynamicClientScopePolicy.HasOptIn(new Dictionary<string, object?>()));
    }

    [Fact]
    public void A_disabled_or_deleted_scope_is_not_requestable_even_with_the_opt_in()
    {
        var disabled = Scope("x", optIn: true);
        disabled.Enabled = false;
        Assert.False(DynamicClientScopePolicy.IsRequestable(disabled));

        var deleted = Scope("y", optIn: true);
        deleted.IsDeleted = true;
        Assert.False(DynamicClientScopePolicy.IsRequestable(deleted));
    }

    [Fact]
    public void Nothing_declared_means_the_whole_requestable_set()
    {
        var requestable = new[] { "openid", "profile", "lists.read" };
        Assert.Equal(requestable, DynamicClientScopePolicy.Resolve(Array.Empty<string>(), requestable));
    }

    [Fact]
    public void A_declaration_is_an_upper_bound_intersected_in_requestable_order()
    {
        var requestable = new[] { "openid", "profile", "lists.read", "lists.write" };
        var declared = new[] { "lists.write", "admin", "openid", "no-such-scope" };
        Assert.Equal(new[] { "openid", "lists.write" }, DynamicClientScopePolicy.Resolve(declared, requestable));
    }

    [Fact]
    public void A_declaration_with_nothing_requestable_in_it_yields_the_empty_set()
    {
        Assert.Empty(DynamicClientScopePolicy.Resolve(new[] { "admin" }, new[] { "openid", "lists.read" }));
    }

    private static OAuthScopeState Scope(string name, bool optIn = false, Guid? appId = null)
    {
        var scope = new OAuthScopeState { Id = Guid.NewGuid(), Name = name, AppId = appId };
        if (optIn)
            scope.Properties[ScopePropertyKeys.AllowDynamicRegistrationClients] = JsonSerializer.SerializeToElement(true);
        return scope;
    }
}
