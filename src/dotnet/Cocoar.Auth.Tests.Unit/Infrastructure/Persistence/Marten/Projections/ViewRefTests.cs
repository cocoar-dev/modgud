using Cocoar.Auth.Infrastructure.Persistence.Marten.Projections;

namespace Cocoar.Auth.Tests.Unit.Infrastructure.Persistence.Marten.Projections;

/// <summary>
/// Pins the value-equality contract of <see cref="ViewRef"/>. Embedded refs
/// flow through projection rebuilds; relying on reference equality would silently
/// duplicate rows.
/// </summary>
public class ViewRefTests
{
    [Fact]
    public void Two_refs_with_same_fields_are_equal()
    {
        var id = Guid.NewGuid();
        var a = new ViewRef { Id = id, Label = "Alice", PrincipalType = "Person" };
        var b = new ViewRef { Id = id, Label = "Alice", PrincipalType = "Person" };

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Different_label_makes_refs_unequal()
    {
        var id = Guid.NewGuid();
        var a = new ViewRef { Id = id, Label = "Alice" };
        var b = new ViewRef { Id = id, Label = "Bob" };

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Different_principal_type_makes_refs_unequal()
    {
        var id = Guid.NewGuid();
        var a = new ViewRef { Id = id, Label = "Team", PrincipalType = "Person" };
        var b = new ViewRef { Id = id, Label = "Team", PrincipalType = "Group" };

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Null_principal_type_is_supported_for_non_principal_refs()
    {
        var id = Guid.NewGuid();
        var a = new ViewRef { Id = id, Label = "Customer X", PrincipalType = null };
        var b = new ViewRef { Id = id, Label = "Customer X", PrincipalType = null };

        Assert.Equal(a, b);
    }

    [Fact]
    public void Defaults_are_empty_guid_and_null_strings()
    {
        var v = new ViewRef();
        Assert.Equal(Guid.Empty, v.Id);
        Assert.Null(v.Label);
        Assert.Null(v.PrincipalType);
    }
}
