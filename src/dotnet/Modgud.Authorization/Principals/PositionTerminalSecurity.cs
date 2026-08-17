using System.Text.Json;
using System.Text.Json.Serialization;

namespace Modgud.Authorization.Principals;

/// <summary>
/// Stable, open activation-proof identifiers. Stored values deliberately remain
/// strings so future proof plug-ins do not require a shared enum deployment.
/// </summary>
public static class ActivationProofMethodIds
{
    public const string PersonalPasskey = "personal-passkey";
    public const string PersonalPassword = "personal-password";
    public const string PersonalEmailOtp = "personal-email-otp";
    public const string PositionToken = "position-token";
    public const string TeamSecret = "team-secret";

    public static readonly IReadOnlyDictionary<string, ProofMethodDescriptor> Known =
        new Dictionary<string, ProofMethodDescriptor>(StringComparer.Ordinal)
        {
            [PersonalPasskey] = new(
                PersonalPasskey,
                ProofCapability.IdentifiedActor |
                ProofCapability.PhishingResistant |
                ProofCapability.IndividuallyRevocable,
                ActivationProofOwnerKind.Personal,
                IsAvailable: true),
            [PersonalPassword] = new(
                PersonalPassword,
                ProofCapability.IdentifiedActor,
                ActivationProofOwnerKind.Personal,
                IsAvailable: true),
            [PersonalEmailOtp] = new(
                PersonalEmailOtp,
                ProofCapability.IdentifiedActor,
                ActivationProofOwnerKind.Personal,
                IsAvailable: true),
            [PositionToken] = new(
                PositionToken,
                ProofCapability.PhishingResistant |
                ProofCapability.IndividuallyRevocable,
                ActivationProofOwnerKind.PositionCredential,
                IsAvailable: true),
        };
}

/// <summary>Stable, open device-binding identifiers.</summary>
public static class DeviceBindingIds
{
    public const string Dpop = "dpop";
    public const string ClientSecret = "client-secret";
    public const string None = "none";

    public static readonly IReadOnlyDictionary<string, DeviceBindingDescriptor> Known =
        new Dictionary<string, DeviceBindingDescriptor>(StringComparer.Ordinal)
        {
            [Dpop] = new(Dpop,
                BindingCapability.DeviceIdentity | BindingCapability.SenderConstrained,
                IsAvailable: true),
            [ClientSecret] = new(ClientSecret, BindingCapability.DeviceIdentity, IsAvailable: true),
            [None] = new(None, BindingCapability.None, IsAvailable: true),
        };
}

public sealed record ProofMethodDescriptor(
    string MethodId,
    ProofCapability Capabilities,
    ActivationProofOwnerKind OwnerKind,
    bool IsAvailable);

public sealed record DeviceBindingDescriptor(
    string BindingId,
    BindingCapability Capabilities,
    bool IsAvailable);

public enum ActivationProofOwnerKind
{
    Personal,
    PositionCredential,
    SharedSecret,
}

[Flags]
[JsonConverter(typeof(ProofCapabilityJsonConverter))]
public enum ProofCapability
{
    None = 0,
    IdentifiedActor = 1,
    PhishingResistant = 2,
    IndividuallyRevocable = 4,
}

[Flags]
[JsonConverter(typeof(BindingCapabilityJsonConverter))]
public enum BindingCapability
{
    None = 0,
    DeviceIdentity = 1,
    SenderConstrained = 2,
}

/// <summary>
/// Central rules for writes and execution. Reads intentionally do not call
/// these helpers: unknown stored IDs must survive round-trips unchanged.
/// </summary>
public static class PositionTerminalSecurity
{
    public static bool TryGetWritableProof(string methodId, out ProofMethodDescriptor descriptor)
    {
        if (ActivationProofMethodIds.Known.TryGetValue(methodId, out var value) && value.IsAvailable)
        {
            descriptor = value;
            return true;
        }

        descriptor = null!;
        return false;
    }

    public static bool TryGetWritableBinding(string bindingId, out DeviceBindingDescriptor descriptor)
    {
        if (DeviceBindingIds.Known.TryGetValue(bindingId, out var value) && value.IsAvailable)
        {
            descriptor = value;
            return true;
        }

        descriptor = null!;
        return false;
    }

    public static bool ProofMeetsFloor(string methodId, ProofCapability required)
        => ActivationProofMethodIds.Known.TryGetValue(methodId, out var descriptor)
           && (descriptor.Capabilities & required) == required;

    public static bool BindingMeetsFloor(string bindingId, BindingCapability required)
        => DeviceBindingIds.Known.TryGetValue(bindingId, out var descriptor)
           && (descriptor.Capabilities & required) == required;
}

public sealed class ProofCapabilityJsonConverter : FlagArrayJsonConverter<ProofCapability>;

public sealed class BindingCapabilityJsonConverter : FlagArrayJsonConverter<BindingCapability>;

/// <summary>Serializes flags as an array of stable enum names, never as a
/// comma-delimited pseudo-enum string.</summary>
public abstract class FlagArrayJsonConverter<TEnum> : JsonConverter<TEnum>
    where TEnum : struct, Enum
{
    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException($"{typeof(TEnum).Name} must be a JSON string array.");

        ulong combined = 0;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.String ||
                !Enum.TryParse<TEnum>(reader.GetString(), ignoreCase: false, out var value) ||
                !Enum.IsDefined(value))
                throw new JsonException($"Unknown {typeof(TEnum).Name} capability '{reader.GetString()}'.");

            combined |= Convert.ToUInt64(value);
        }

        if (reader.TokenType != JsonTokenType.EndArray)
            throw new JsonException($"Unterminated {typeof(TEnum).Name} capability array.");

        return (TEnum)Enum.ToObject(typeof(TEnum), combined);
    }

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
    {
        var combined = Convert.ToUInt64(value);
        writer.WriteStartArray();
        foreach (var candidate in Enum.GetValues<TEnum>())
        {
            var bits = Convert.ToUInt64(candidate);
            if (bits == 0 || (bits & (bits - 1)) != 0) continue;
            if ((combined & bits) == bits) writer.WriteStringValue(candidate.ToString());
        }
        writer.WriteEndArray();
    }
}
