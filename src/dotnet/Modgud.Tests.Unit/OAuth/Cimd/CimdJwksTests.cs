using System.Text.Json.Nodes;
using Modgud.Infrastructure.OpenIddict.Cimd;

namespace Modgud.Tests.Unit.OAuth.Cimd;

/// <summary>DCR/CIMD-B5-05 — private key material anywhere in the set fails the whole set,
/// also after the 20 usable keys the filter keeps.</summary>
public class CimdJwksTests
{
    private static JsonObject PublicRsa(int i) => new() { ["kty"] = "RSA", ["kid"] = $"k{i}", ["n"] = "AQAB", ["e"] = "AQAB" };

    [Fact]
    public void A_private_key_after_twenty_usable_keys_fails_the_set()
    {
        var keys = new JsonArray();
        for (var i = 0; i < 20; i++) keys.Add(PublicRsa(i));
        var leaked = PublicRsa(20);
        leaked["d"] = "secret";
        keys.Add(leaked);

        var ok = CimdJwks.TryFilter(new JsonObject { ["keys"] = keys }.ToJsonString(), out var filtered, out var error);

        Assert.False(ok);
        Assert.Null(filtered);
        Assert.Contains("private key material", error);
    }

    [Fact]
    public void More_than_twenty_public_keys_are_capped_not_refused()
    {
        var keys = new JsonArray();
        for (var i = 0; i < 25; i++) keys.Add(PublicRsa(i));

        Assert.True(CimdJwks.TryFilter(new JsonObject { ["keys"] = keys }.ToJsonString(), out var filtered, out _));
        Assert.Equal(20, JsonNode.Parse(filtered!)!["keys"]!.AsArray().Count);
    }
}
