using Microsoft.Extensions.Logging.Abstractions;
using TimeToDo.Api.Tests.Infrastructure;
using TimeToDo.Authentication.Identity.ExternalAuth;

namespace TimeToDo.Api.Tests.ExternalAuth;

/// <summary>
/// Unit tests for the user-update-script runner. The runner itself has no DB
/// or HTTP dependency, but we keep the tests in the integration collection so
/// the shared fixture initializes consistently with the rest of the suite.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class UserUpdateScriptRunnerTests : IntegrationTestBase
{
    public UserUpdateScriptRunnerTests(SharedPostgresFixture fixture) : base(fixture) { }

    private static UserUpdateScriptRunner NewRunner() =>
        new(NullLogger<UserUpdateScriptRunner>.Instance);

    [Fact]
    public void HappyPath_MapsStandardClaimsToPatch()
    {
        var runner = NewRunner();
        var script = """
            (claims) => ({
              firstname: claims.given_name?.trim(),
              lastname: claims.family_name?.trim(),
              email: claims.email,
              acronym: (claims.given_name?.[0] ?? '') + (claims.family_name?.[0] ?? '')
            })
        """;

        var raw = new Dictionary<string, object?>
        {
            ["given_name"] = "Alice",
            ["family_name"] = "Anderson",
            ["email"] = "alice@acme.com",
        };

        var result = runner.Run(script, raw);

        Assert.True(result.Succeeded);
        Assert.Null(result.Error);
        Assert.Equal(FieldPresence.Value, result.Firstname.Presence);
        Assert.Equal("Alice", result.Firstname.Value);
        Assert.Equal("Anderson", result.Lastname.Value);
        Assert.Equal("alice@acme.com", result.Email.Value);
        Assert.Equal("AA", result.Acronym.Value);
    }

    [Fact]
    public void ScriptOmittingField_MarksAsNotSet()
    {
        var runner = NewRunner();
        var script = """
            (claims) => ({ firstname: claims.given_name })
        """;

        var result = runner.Run(script, new Dictionary<string, object?>
        {
            ["given_name"] = "Alice",
        });

        Assert.True(result.Succeeded);
        Assert.Equal(FieldPresence.Value, result.Firstname.Presence);
        // Lastname / Email / Acronym not returned by the script → NotSet
        Assert.Equal(FieldPresence.NotSet, result.Lastname.Presence);
        Assert.Equal(FieldPresence.NotSet, result.Email.Presence);
        Assert.Equal(FieldPresence.NotSet, result.Acronym.Presence);
    }

    [Fact]
    public void ExplicitNull_MarksAsClear()
    {
        var runner = NewRunner();
        var script = "(claims) => ({ acronym: null })";

        var result = runner.Run(script, new Dictionary<string, object?>());

        Assert.True(result.Succeeded);
        Assert.Equal(FieldPresence.Null, result.Acronym.Presence);
        Assert.Null(result.Acronym.Value);
    }

    [Fact]
    public void EmptyStringEmittedByScript_CollapsesToNotSet()
    {
        // Empty string from a script is almost always an accident (concat with
        // missing parts). "Clear" should be explicit via null. Empty = NotSet.
        var runner = NewRunner();
        var script = """
            (claims) => ({
              acronym: (claims.given_name?.[0] ?? '') + (claims.family_name?.[0] ?? '')
            })
        """;

        var result = runner.Run(script, new Dictionary<string, object?>());

        Assert.True(result.Succeeded);
        Assert.Equal(FieldPresence.NotSet, result.Acronym.Presence);
    }

    [Fact]
    public void TrimmingInsideScript_Applied()
    {
        var runner = NewRunner();
        var script = "(claims) => ({ firstname: claims.given_name?.trim() })";

        var result = runner.Run(script, new Dictionary<string, object?>
        {
            ["given_name"] = "  Alice  ",
        });

        Assert.True(result.Succeeded);
        Assert.Equal("Alice", result.Firstname.Value);
    }

    [Fact]
    public void ScriptThrows_ReportsFailureWithoutCrashing()
    {
        var runner = NewRunner();
        var script = "(claims) => { throw new Error('boom'); }";

        var result = runner.Run(script, new Dictionary<string, object?>());

        Assert.False(result.Succeeded);
        Assert.Contains("boom", result.Error);
        Assert.Equal(FieldPresence.NotSet, result.Firstname.Presence);
        Assert.Null(result.ScriptOutput);
    }

    [Fact]
    public void EmptyScript_IsFailure()
    {
        var runner = NewRunner();
        var result = runner.Run(string.Empty, new Dictionary<string, object?>());

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void ScriptReturnsNonObject_IsFailure()
    {
        var runner = NewRunner();
        var result = runner.Run("(c) => 42", new Dictionary<string, object?>());

        Assert.False(result.Succeeded);
        Assert.Contains("object", result.Error);
    }

    [Fact]
    public void UnknownFieldsInScriptOutput_AreIgnored_ButCapturedInScriptOutput()
    {
        // Script returns roles/groups/etc. — the runner accepts the object but
        // only the four recognized fields map to the patch. ScriptOutput gets
        // the full object for debugging.
        var runner = NewRunner();
        var script = """
            (claims) => ({
              firstname: 'Alice',
              groups: ['Admins'],
              department: 'IT'
            })
        """;

        var result = runner.Run(script, new Dictionary<string, object?>());

        Assert.True(result.Succeeded);
        Assert.Equal("Alice", result.Firstname.Value);
        Assert.NotNull(result.ScriptOutput);
        var json = result.ScriptOutput!.RootElement.GetRawText();
        Assert.Contains("groups", json);
        Assert.Contains("department", json);
    }
}
