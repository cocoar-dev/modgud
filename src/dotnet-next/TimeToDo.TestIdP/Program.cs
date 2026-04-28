using System.Text.Json;
using TimeToDo.TestIdP;
using TimeToDo.TestIdP.Config;

namespace TimeToDo.TestIdP;

// Explicit Program class (not top-level) so both TestIdP and the main API
// can coexist in test assemblies without a duplicate-Program ambiguity.
internal sealed class Program
{
    public static void Main(string[] args)
    {
        var configPath = Environment.GetEnvironmentVariable("TESTIDP_CONFIG")
            ?? Path.Combine(AppContext.BaseDirectory, "data", "test-idp-config.json");
        var localPath = Path.Combine(AppContext.BaseDirectory, "data", "test-idp-config.local.json");
        var activePath = File.Exists(localPath) ? localPath : configPath;
        if (!File.Exists(activePath))
            throw new InvalidOperationException(
                $"TestIdP config not found at '{activePath}'. Set TESTIDP_CONFIG or create the default file.");

        Console.WriteLine($"[TestIdP] Loading config from: {activePath}");
        var config = JsonSerializer.Deserialize<TestIdpConfig>(
            File.ReadAllText(activePath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("Config deserialized to null.");

        var app = TestIdpHost.Build(config, args);
        app.Run();
    }
}
