using System.Collections.Immutable;
using System.Text.Json;
using Modgud.Domain.OAuth.Applications;
using Modgud.Infrastructure.OpenIddict;

namespace Modgud.Tests.Unit.OpenIddict;

/// <summary>EG-04 — the client Enabled switch fails closed on any value that is not a JSON boolean.</summary>
public sealed class OAuthClientEnabledTests
{
    [Fact]
    public void A_client_without_the_property_is_enabled() =>
        Assert.True(OAuthClientEnabled.IsEnabled(ImmutableDictionary<string, JsonElement>.Empty));

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("\"true\"", false)]
    [InlineData("\"false\"", false)]
    [InlineData("null", false)]
    [InlineData("1", false)]
    [InlineData("{}", false)]
    public void Only_a_json_true_enables(string json, bool expected)
    {
        var properties = ImmutableDictionary<string, JsonElement>.Empty
            .Add(OAuthApplicationPropertyKeys.Enabled, JsonDocument.Parse(json).RootElement.Clone());
        Assert.Equal(expected, OAuthClientEnabled.IsEnabled(properties));
    }
}
