using Modgud.Api.ExtensionMethods;

namespace Modgud.Api.Tests.Security;

/// <summary>
/// B10-DOC-02 — the docs path guard compared with a plain prefix: a sibling directory whose name
/// starts with the docs root's ("docs-private" next to "docs") counted as inside it.
/// </summary>
public class DocsPathGuardTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "wwwroot", "docs");

    [Theory]
    [InlineData("index.html", true)]
    [InlineData("guide/intro.html", true)]
    [InlineData("", true)]
    [InlineData("../docs-private/secret.html", false)]
    [InlineData("../other/file.html", false)]
    public void Only_paths_under_the_docs_directory_are_inside(string relative, bool inside)
    {
        var resolved = Path.GetFullPath(Path.Combine(Root, relative));
        Assert.Equal(inside, DocsMiddleware.IsInsideRoot(Root, resolved));
    }
}
