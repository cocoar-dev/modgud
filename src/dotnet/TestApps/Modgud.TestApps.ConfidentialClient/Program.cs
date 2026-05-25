using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

// Modgud.TestApps.ConfidentialClient — minimal Client-Credentials demo.
//
// Demonstrates the M2M flow end-to-end:
//   1. POST /connect/token (grant_type=client_credentials)
//   2. Call ResourceApi /scoped with the bearer token
//
// Reuses the seeded `demo-backend` client from data/demo-seed.json.
// Designed to be runnable standalone (`dotnet run`) and trivially
// portable into xUnit Testcontainers integration tests later.

var Authority = Environment.GetEnvironmentVariable("TESTAPPS_AUTHORITY") ?? "http://localhost:9099";
var ResourceApi = Environment.GetEnvironmentVariable("TESTAPPS_RESOURCEAPI") ?? "http://localhost:7081";
var ClientId = Environment.GetEnvironmentVariable("TESTAPPS_CLIENTID") ?? "demo-backend";
var ClientSecret = Environment.GetEnvironmentVariable("TESTAPPS_CLIENTSECRET") ?? "demo-backend-secret-please-rotate";
var Scopes = Environment.GetEnvironmentVariable("TESTAPPS_SCOPES") ?? "demo.read demo.write";

using var http = new HttpClient();

Console.WriteLine($"→ POST {Authority}/connect/token (client_credentials)");
var tokenResponse = await http.PostAsync($"{Authority}/connect/token",
    new FormUrlEncodedContent(new Dictionary<string, string>
    {
        ["grant_type"] = "client_credentials",
        ["client_id"] = ClientId,
        ["client_secret"] = ClientSecret,
        ["scope"] = Scopes,
    }));

var tokenBody = await tokenResponse.Content.ReadAsStringAsync();
if (!tokenResponse.IsSuccessStatusCode)
{
    Console.Error.WriteLine($"   Token request failed ({(int)tokenResponse.StatusCode}):");
    Console.Error.WriteLine(tokenBody);
    return 1;
}

using var tokenJson = JsonDocument.Parse(tokenBody);
var accessToken = tokenJson.RootElement.GetProperty("access_token").GetString()!;
var expiresIn = tokenJson.RootElement.GetProperty("expires_in").GetInt32();
Console.WriteLine($"   ✓ access_token obtained (expires in {expiresIn}s)");

http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

Console.WriteLine($"→ GET  {ResourceApi}/scoped");
var scoped = await http.GetAsync($"{ResourceApi}/scoped");
var scopedBody = await scoped.Content.ReadAsStringAsync();
Console.WriteLine($"   {(int)scoped.StatusCode}  {scopedBody}");

Console.WriteLine($"→ GET  {ResourceApi}/admin (expected 403 — demo-backend has no demo.admin)");
var admin = await http.GetAsync($"{ResourceApi}/admin");
Console.WriteLine($"   {(int)admin.StatusCode}");

return scoped.IsSuccessStatusCode ? 0 : 2;
