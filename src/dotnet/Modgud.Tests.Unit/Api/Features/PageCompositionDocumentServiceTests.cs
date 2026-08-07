using Modgud.Api.Features.Admin;
using Modgud.Domain.Realms;

namespace Modgud.Tests.Unit.Api.Features;

public class PageCompositionDocumentServiceTests
{
    [Fact]
    public void Composition_root_must_be_one_non_page_element()
    {
        Assert.False(PageCompositionDocumentService.ValidateCompositionRoot(
            """{"id":"root","type":"page","children":[]}""", out var pageError));
        Assert.Contains("disallowed type", pageError);

        Assert.True(PageCompositionDocumentService.ValidateCompositionRoot(
            """{"id":"root","type":"stack","name":"root","props":{"direction":"column"},"children":[]}""",
            out var validError), validError);
    }

    [Fact]
    public void Publish_compiles_materialized_instances_to_repository_free_runtime_json()
    {
        var compositions = new[]
        {
            Definition("brand", 1,
                """{"id":"template-root","type":"stack","name":"brand","props":{},"children":[]}"""),
        };
        const string authoring = """
        {
          "id":"page","type":"page","schemaVersion":5,"children":[
            {
              "id":"instance","type":"stack","name":"brand2","props":{},
              "composition":{"id":"brand","version":"1"},
              "compositionOrigins":[{"id":"brand","sourceNodeId":"template-root"}],
              "children":[
                {"id":"title","type":"heading","name":"title","props":{"text":"Hello"},"compositionOrigins":[{"id":"brand","sourceNodeId":"title"}]}
              ]
            }
          ]
        }
        """;

        Assert.True(PageCompositionDocumentService.ValidateAndCompilePage(
            "login", authoring, compositions, out var runtime, out var error), error);
        Assert.DoesNotContain("composition", runtime, StringComparison.Ordinal);
        Assert.Contains("\"id\":\"instance\"", runtime, StringComparison.Ordinal);
        Assert.Contains("\"id\":\"title\"", runtime, StringComparison.Ordinal);
    }

    [Fact]
    public void Exact_historic_version_remains_loadable()
    {
        var composition = Definition("brand", 1,
            """{"id":"v1","type":"stack","name":"brand","props":{},"children":[]}""");
        composition.Versions.Add(new PageCompositionVersion
        {
            Number = 2,
            Root = """{"id":"v2","type":"stack","name":"brand","props":{},"children":[]}""",
        });
        const string page = """
        {"id":"page","type":"page","schemaVersion":5,"children":[
          {"id":"instance","type":"stack","name":"brand2","props":{},"composition":{"id":"brand","version":"1"},"children":[]}
        ]}
        """;

        Assert.True(PageCompositionDocumentService.ValidateReferences(page, [composition], out var error), error);
    }

    [Fact]
    public void Missing_versions_and_nested_cycles_are_rejected()
    {
        const string missingPage = """
        {"id":"page","type":"page","schemaVersion":5,"children":[
          {"id":"instance","type":"stack","name":"instance","props":{},"composition":{"id":"missing","version":"1"},"children":[]}
        ]}
        """;
        Assert.False(PageCompositionDocumentService.ValidateReferences(missingPage, [], out var missingError));
        Assert.Contains("missing@1 is missing", missingError);

        var a = Definition("a", 1,
            """{"id":"a-root","type":"stack","name":"a","props":{},"composition":{"id":"b","version":"1"},"children":[]}""");
        var b = Definition("b", 1,
            """{"id":"b-root","type":"stack","name":"b","props":{},"composition":{"id":"a","version":"1"},"children":[]}""");
        const string cyclePage = """
        {"id":"page","type":"page","schemaVersion":5,"children":[
          {"id":"instance","type":"stack","name":"instance","props":{},"composition":{"id":"a","version":"1"},"children":[]}
        ]}
        """;

        Assert.False(PageCompositionDocumentService.ValidateReferences(cyclePage, [a, b], out var cycleError));
        Assert.Contains("cycle detected", cycleError, StringComparison.OrdinalIgnoreCase);
    }

    private static PageComposition Definition(string id, int version, string root) => new()
    {
        Id = id,
        Name = id,
        Versions = [new PageCompositionVersion { Number = version, Root = root }],
    };
}
