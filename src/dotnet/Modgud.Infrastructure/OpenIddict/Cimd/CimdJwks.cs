using System.Text.Json;
using System.Text.Json.Nodes;

namespace Modgud.Infrastructure.OpenIddict.Cimd;

/// <summary>
/// Reads the public key set a <c>private_key_jwt</c> CIMD client publishes —
/// inline (<c>jwks</c>) or at its <c>jwks_uri</c>. Unlike an admin-registered
/// set, this one is maintained by the client for every server it talks to, so
/// it may carry keys Modgud has no use for (encryption keys, key types it does
/// not verify with); those are skipped, not fatal. What does fail the set is
/// private key material — the draft forbids it, and its presence means the
/// publisher has leaked a key, so nothing signed by that set can be trusted.
/// </summary>
public static class CimdJwks
{
    private const int MaxKeys = 20;

    private static readonly string[] PrivateMembers = ["d", "p", "q", "dp", "dq", "qi", "oth", "k"];

    /// <summary>Filters <paramref name="json"/> (an RFC 7517 set) to the public
    /// RSA/EC signing keys and returns them re-serialized, or sets
    /// <paramref name="error"/>.</summary>
    public static bool TryFilter(string json, out string? filtered, out string? error)
    {
        filtered = null;
        error = null;

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json.TrimStart((char)0xFEFF));
        }
        catch (JsonException)
        {
            error = "key set is not valid JSON.";
            return false;
        }

        if (root is not JsonObject obj || obj["keys"] is not JsonArray keys)
        {
            error = "key set is not an object with a \"keys\" array (RFC 7517).";
            return false;
        }

        var usable = new JsonArray();
        foreach (var node in keys)
        {
            if (node is not JsonObject key) continue;
            if (PrivateMembers.Any(key.ContainsKey))
            {
                error = "key set contains private key material.";
                return false;
            }

            var kty = (key["kty"] as JsonValue)?.TryGetValue<string>(out var k) == true ? k : null;
            var use = (key["use"] as JsonValue)?.TryGetValue<string>(out var u) == true ? u : null;
            if (use is not null && use != "sig") continue;
            var complete = kty switch
            {
                "RSA" => key.ContainsKey("n") && key.ContainsKey("e"),
                "EC" => key.ContainsKey("crv") && key.ContainsKey("x") && key.ContainsKey("y"),
                _ => false,
            };
            if (!complete) continue;

            usable.Add(key.DeepClone());
            if (usable.Count == MaxKeys) break;
        }

        if (usable.Count == 0)
        {
            error = "key set has no usable signing key (public RSA or EC).";
            return false;
        }

        filtered = new JsonObject { ["keys"] = usable }.ToJsonString();
        return true;
    }
}
