using Modgud.Authentication.Registration;

namespace Modgud.Tests.Unit.Authentication;

/// <summary>ADR 0006 — "one pending record per address" is a property of the key.</summary>
public class PendingRegistrationIdTests
{
    [Fact]
    public void Same_address_always_maps_to_the_same_id()
    {
        Assert.Equal(PendingRegistration.IdFor("alex@example.test"), PendingRegistration.IdFor("alex@example.test"));
    }

    [Theory]
    [InlineData("Alex@Example.Test")]
    [InlineData("  alex@example.test  ")]
    [InlineData("ALEX@EXAMPLE.TEST")]
    public void Case_and_surrounding_whitespace_do_not_change_the_id(string variant)
    {
        Assert.Equal(PendingRegistration.IdFor("alex@example.test"), PendingRegistration.IdFor(variant));
    }

    [Fact]
    public void Different_addresses_map_to_different_ids()
    {
        Assert.NotEqual(PendingRegistration.IdFor("alex@example.test"), PendingRegistration.IdFor("alex@example.org"));
        Assert.NotEqual(PendingRegistration.IdFor("alex@example.test"), PendingRegistration.IdFor("alex+1@example.test"));
    }

    [Fact]
    public void Id_is_shaped_like_a_random_guid()
    {
        var text = PendingRegistration.IdFor("alex@example.test").ToString();
        // version nibble 4, RFC 4122 variant (8/9/a/b)
        Assert.Equal('4', text[14]);
        Assert.Contains(text[19], "89ab");
        Assert.NotEqual(Guid.Empty, PendingRegistration.IdFor("x@y.z"));
    }

    [Fact]
    public void Attempt_cap_only_applies_when_configured()
    {
        var code = new PendingRegistration { MaxAttempts = 3, Attempts = 3 };
        var link = new PendingRegistration { MaxAttempts = 0, Attempts = 1_000 };
        Assert.True(code.HasExceededAttempts);
        Assert.False(link.HasExceededAttempts);
    }
}
