using Modgud.Domain.OAuth.Scopes;

namespace Modgud.Tests.Unit.OAuth;

/// <summary>
/// Pins the wire-format property keys used on OpenIddict scope rows. These keys are persisted
/// in the scope's Properties dictionary; renaming any of them silently orphans the value in
/// the database and the admin UI starts showing "default" for all existing scopes.
///
/// (StandardScopes is covered by <see cref="StandardScopesTests"/>.)
/// </summary>
public class ScopePropertyKeysTests
{
    [Fact]
    public void Enabled_value_is_pinned() =>
        Assert.Equal("cocoar:enabled", ScopePropertyKeys.Enabled);

    [Fact]
    public void Required_value_is_pinned() =>
        Assert.Equal("cocoar:required", ScopePropertyKeys.Required);

    [Fact]
    public void Emphasize_value_is_pinned() =>
        Assert.Equal("cocoar:emphasize", ScopePropertyKeys.Emphasize);

    [Fact]
    public void ShowInDiscoveryDocument_value_is_pinned() =>
        Assert.Equal("cocoar:show_in_discovery_document", ScopePropertyKeys.ShowInDiscoveryDocument);

    [Fact]
    public void UserClaims_value_is_pinned() =>
        Assert.Equal("cocoar:user_claims", ScopePropertyKeys.UserClaims);

    [Fact]
    public void All_keys_use_the_cocoar_prefix()
    {
        // Scope properties live next to OpenIddict's own ones in the same dictionary —
        // the "cocoar:" namespace is the only thing keeping them apart.
        var keys = new[]
        {
            ScopePropertyKeys.Enabled,
            ScopePropertyKeys.Required,
            ScopePropertyKeys.Emphasize,
            ScopePropertyKeys.ShowInDiscoveryDocument,
            ScopePropertyKeys.UserClaims,
        };

        foreach (var k in keys)
            Assert.StartsWith("cocoar:", k);
    }

    [Fact]
    public void All_keys_are_unique()
    {
        var keys = new[]
        {
            ScopePropertyKeys.Enabled,
            ScopePropertyKeys.Required,
            ScopePropertyKeys.Emphasize,
            ScopePropertyKeys.ShowInDiscoveryDocument,
            ScopePropertyKeys.UserClaims,
        };
        Assert.Equal(keys.Length, keys.Distinct().Count());
    }
}
