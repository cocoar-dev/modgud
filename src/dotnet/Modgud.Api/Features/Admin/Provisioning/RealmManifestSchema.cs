using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using Modgud.Domain.Common;

namespace Modgud.Api.Features.Admin.Provisioning;

/// <summary>
/// Builds the JSON Schema for <see cref="RealmManifest"/> served at
/// <c>GET /api/admin/realms/manifest-schema</c>. Generated from the live type via
/// <see cref="JsonSchemaExporter"/> using the API's own <see cref="JsonSerializerOptions"/>,
/// so the schema's property names + nullability always match the actual wire contract (no
/// drift). Each node's <c>description</c> is pulled from the <see cref="DescriptionAttribute"/>
/// on the corresponding property/type, and a worked example is attached at the root — enough
/// for a consumer (or an agent) to author a valid manifest from the fetched schema alone.
/// </summary>
public static class RealmManifestSchema
{
    public static JsonNode Build(JsonSerializerOptions serializerOptions)
    {
        var exporterOptions = new JsonSchemaExporterOptions
        {
            // A non-nullable reference-typed property is a genuine "required" field.
            TreatNullObliviousAsNonNullable = true,
            TransformSchemaNode = (context, schema) =>
                InjectDescriptions(context, MapOptional(context, schema)),
        };

        var schema = serializerOptions.GetJsonSchemaAsNode(typeof(RealmManifest), exporterOptions);

        if (schema is JsonObject root)
        {
            root["$schema"] = "https://json-schema.org/draft/2020-12/schema";
            root["title"] = "Modgud realm manifest";
            root["examples"] = new JsonArray(Example());
        }

        return schema;
    }

    /// <summary>
    /// <see cref="Optional{T}"/> fields carry the manifest's merge-patch presence semantics
    /// (absent = unchanged, explicit null = clear) through a custom converter the schema
    /// exporter can't see into — it emits an accept-anything schema for them. Replace that
    /// with the inner type's schema, nullable (the wire shape an author actually writes).
    /// </summary>
    private static JsonNode MapOptional(JsonSchemaExporterContext context, JsonNode schema)
    {
        var type = context.TypeInfo.Type;
        if (!type.IsGenericType || type.GetGenericTypeDefinition() != typeof(Optional<>))
            return schema;

        var arg = type.GetGenericArguments()[0];
        var inner = Nullable.GetUnderlyingType(arg) ?? arg;
        if (inner == typeof(string))
            return new JsonObject { ["type"] = new JsonArray("string", "null") };
        if (inner == typeof(int))
            return new JsonObject { ["type"] = new JsonArray("integer", "null") };
        if (inner == typeof(List<string>))
            return new JsonObject
            {
                ["type"] = new JsonArray("array", "null"),
                ["items"] = new JsonObject { ["type"] = "string" },
            };
        return schema;
    }

    /// <summary>Copies the <see cref="DescriptionAttribute"/> off the property (preferred) or
    /// the type onto the generated schema node's <c>description</c>.</summary>
    private static JsonNode InjectDescriptions(JsonSchemaExporterContext context, JsonNode schema)
    {
        if (schema is not JsonObject obj || obj["description"] is not null)
            return schema;

        var description =
            GetDescription(context.PropertyInfo?.AttributeProvider)
            ?? GetDescription(context.TypeInfo.Type);

        if (description is not null)
            obj["description"] = description;

        return schema;
    }

    private static string? GetDescription(ICustomAttributeProvider? provider) =>
        provider?
            .GetCustomAttributes(typeof(DescriptionAttribute), inherit: false)
            .OfType<DescriptionAttribute>()
            .FirstOrDefault()?
            .Description;

    private static JsonNode Example() => JsonNode.Parse(
        """
        {
          "Realm": {
            "Slug": "acme-test",
            "DisplayName": "Acme Test",
            "Domains": ["acme-test.localhost"],
            "InitialAdmin": { "UserName": "admin", "Email": "admin@acme-test.local" }
          },
          "Apps": [
            { "Slug": "acme", "DisplayName": "Acme",
              "Permissions": [ { "Resource": "invoice", "Action": "read" },
                               { "Resource": "invoice", "Action": "write" } ] }
          ],
          "Apis": [
            { "Name": "acme-api", "App": "acme",
              "Permissions": [ { "Resource": "invoice", "Action": "read" } ] }
          ],
          "Scopes": [
            { "Name": "invoice.read", "App": "acme", "Resources": ["acme-api"] }
          ],
          "Clients": [
            { "ClientId": "acme-web", "ClientType": "confidential",
              "RedirectUris": ["https://acme-test.localhost/cb"],
              "Scopes": ["openid", "invoice.read"],
              "AllowedGrantTypes": ["authorization_code", "refresh_token"],
              "Apps": ["acme"] }
          ],
          "Roles": [
            { "Key": "acme-admin", "Name": "acme-admin", "App": "acme",
              "Permissions": [ { "Resource": "invoice", "Action": "read" },
                               { "Resource": "invoice", "Action": "write" } ] }
          ],
          "Users": [
            { "Key": "alice", "Email": "alice@acme.test", "UserName": "alice", "Password": "Passw0rd!23" }
          ],
          "Groups": [
            { "Name": "Acme Admins", "Members": ["alice"], "Roles": ["acme-admin"], "BoundTo": ["acme"] }
          ],
          "LoginProviders": [
            { "Slug": "corp-idp", "Flavor": "GenericOidc", "DisplayName": "Corp IdP",
              "ClientId": "modgud", "ClientSecret": "secret-from-the-upstream-idp",
              "FlavorData": { "MetadataUri": "https://idp.example.com/.well-known/openid-configuration" } }
          ],
          "Positions": [
            { "AccountName": "gate.porter", "Grants": ["alice"],
              "TerminalPolicy": { "Enabled": true,
                                  "AllowedActivationProofs": ["personal-passkey"],
                                  "AllowedDeviceBindings": ["dpop"],
                                  "StaffingSessionLifetimeMinutes": 60,
                                  "MaximumStaffingSessionLifetimeMinutes": 480 } }
          ]
        }
        """)!;
}
