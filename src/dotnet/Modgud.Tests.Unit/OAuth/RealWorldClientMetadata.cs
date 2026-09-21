namespace Modgud.Tests.Unit.OAuth;

/// <summary>
/// Client metadata that real MCP clients actually present — verbatim, not
/// idealised. The CIMD and DCR policies were first written against documents
/// we wrote ourselves, and each real client that tried broke a release
/// (ephemeral loopback port, missing scope, an extra grant type). When a
/// client changes its metadata, refresh the copy here and keep the date.
///
/// <para>CIMD documents were fetched live from their client_id URL on
/// 2026-09-21. DCR bodies are reconstructed from each client's open source
/// (file named per entry) on the same date; placeholders the client fills at
/// run time (port, scope) are filled with representative values.</para>
/// </summary>
public static class RealWorldClientMetadata
{
    // ── CIMD documents (fetched 2026-09-21) ─────────────────────────────

    public const string ClaudeAiId = "https://claude.ai/oauth/mcp-oauth-client-metadata";
    public const string ClaudeAi = """
        {"client_id":"https://claude.ai/oauth/mcp-oauth-client-metadata","client_name":"Claude","client_uri":"https://claude.ai","redirect_uris":["https://claude.ai/api/mcp/auth_callback"],"grant_types":["authorization_code","refresh_token","urn:ietf:params:oauth:grant-type:jwt-bearer"],"response_types":["code"],"token_endpoint_auth_method":"none"}
        """;

    public const string ClaudeCodeId = "https://claude.ai/oauth/claude-code-client-metadata";
    public const string ClaudeCode = """
        {"client_id":"https://claude.ai/oauth/claude-code-client-metadata","client_name":"Claude Code","client_uri":"https://claude.ai","redirect_uris":["http://localhost/callback","http://127.0.0.1/callback"],"grant_types":["authorization_code","refresh_token"],"response_types":["code"],"token_endpoint_auth_method":"none"}
        """;

    public const string VsCodeId = "https://vscode.dev/oauth/client-metadata.json";
    public const string VsCode = """
        {"client_name":"Visual Studio Code","logo_uri":"https://code.visualstudio.com/assets/branding/code-stable.png","grant_types":["authorization_code","refresh_token","urn:ietf:params:oauth:grant-type:device_code"],"response_types":["code"],"token_endpoint_auth_method":"none","application_type":"native","client_id":"https://vscode.dev/oauth/client-metadata.json","client_uri":"https://vscode.dev/product","redirect_uris":["http://127.0.0.1:33418/","https://vscode.dev/redirect"]}
        """;

    public const string ZedId = "https://zed.dev/oauth/client-metadata.json";
    public const string Zed = """
        {"client_id":"https://zed.dev/oauth/client-metadata.json","client_name":"Zed","client_uri":"https://zed.dev","redirect_uris":["http://127.0.0.1/callback"],"grant_types":["authorization_code","refresh_token"],"response_types":["code"],"token_endpoint_auth_method":"none","logo_uri":"https://cdn.zed.dev/images/logo-dark.png"}
        """;

    public const string GooseId = "https://goose-docs.ai/oauth/client-metadata.json";
    public const string Goose = """
        {"client_id":"https://goose-docs.ai/oauth/client-metadata.json","client_name":"goose","logo_uri":"https://goose-docs.ai/img/logo_light.png","redirect_uris":["http://127.0.0.1/oauth_callback","http://[::1]/oauth_callback"],"grant_types":["authorization_code","refresh_token"],"response_types":["code"],"token_endpoint_auth_method":"none","code_challenge_methods_supported":["S256"]}
        """;

    /// <summary>A private_key_jwt client with a remote <c>jwks_uri</c> — the one
    /// real CIMD client that is not public (redirect as published; per-connector
    /// callbacks are not part of the document).</summary>
    public const string ChatGptId = "https://chatgpt.com/oauth/client.json";
    public const string ChatGpt = """
        {"client_id":"https://chatgpt.com/oauth/client.json","client_uri":"https://chatgpt.com/","redirect_uris":["https://chatgpt.com/connector_platform_oauth_redirect"],"token_endpoint_auth_method":"private_key_jwt","token_endpoint_auth_methods_supported":["none","private_key_jwt"],"grant_types":["authorization_code","refresh_token"],"response_types":["code"],"client_name":"ChatGPT","logo_uri":"https://persistent.oaistatic.com/sonic/misc/openai-logo.png","token_endpoint_auth_signing_alg":"RS256","jwks_uri":"https://chatgpt.com/oauth/jwks.json"}
        """;

    /// <summary>The public CIMD documents, as (client_id, document, display name).</summary>
    public static TheoryData<string, string, string> PublicCimdDocuments => new()
    {
        { ClaudeAiId, ClaudeAi, "Claude" },
        { ClaudeCodeId, ClaudeCode, "Claude Code" },
        { VsCodeId, VsCode, "Visual Studio Code" },
        { ZedId, Zed, "Zed" },
        { GooseId, Goose, "goose" },
    };

    // ── DCR request bodies (from source, 2026-09-21) ────────────────────

    /// <summary>microsoft/vscode — src/vs/base/common/oauth.ts, fetchDynamicRegistration().</summary>
    public const string VsCodeDcr = """
        {"client_name":"Visual Studio Code","client_uri":"https://code.visualstudio.com","grant_types":["authorization_code","refresh_token"],"response_types":["code"],"redirect_uris":["https://insiders.vscode.dev/redirect","https://vscode.dev/redirect","http://127.0.0.1/","http://127.0.0.1:33418/"],"scope":"openid","token_endpoint_auth_method":"none","application_type":"native"}
        """;

    /// <summary>zed-industries/zed — crates/context_server/src/oauth.rs,
    /// dcr_registration_body(): registers the ephemeral port it got.</summary>
    public const string ZedDcr = """
        {"client_name":"Zed","redirect_uris":["http://127.0.0.1:49152/callback"],"grant_types":["authorization_code","refresh_token"],"response_types":["code"],"token_endpoint_auth_method":"none"}
        """;

    /// <summary>modelcontextprotocol/inspector — core/auth/providers.ts; the
    /// scope is an empty string when none is configured.</summary>
    public const string McpInspectorDcr = """
        {"redirect_uris":["http://localhost:6274/oauth/callback"],"token_endpoint_auth_method":"none","grant_types":["authorization_code","refresh_token"],"response_types":["code"],"client_name":"MCP Inspector","client_uri":"https://github.com/modelcontextprotocol/inspector","scope":"","application_type":"native"}
        """;

    public static TheoryData<string, string> DcrBodies => new()
    {
        { "Visual Studio Code", VsCodeDcr },
        { "Zed", ZedDcr },
        { "MCP Inspector", McpInspectorDcr },
    };
}
