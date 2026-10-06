using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Modgud.Api.ExtensionMethods;
using Modgud.Api.Helper;
using Modgud.Api.Middleware;

namespace Modgud.Tests.Unit.Api;

/// <summary>
/// The SPA's page shell (the index.html fallback) carries no data and must load for a
/// signed-in session below the required sign-in level: otherwise the sign-in enforcement
/// answers the page request itself with its JSON body, and the user sees that instead of
/// the step-up or second-factor setup the SPA would show.
/// </summary>
[Collection(nameof(PathHelperCollection))]
public class SpaShellTests
{
    [Fact]
    public void The_page_shell_fallback_is_anonymous()
    {
        var root = Directory.CreateTempSubdirectory("modgud-spa-");
        var original = PathHelper.ContentPath;
        try
        {
            Directory.CreateDirectory(Path.Combine(root.FullName, "wwwroot"));
            File.WriteAllText(Path.Combine(root.FullName, "wwwroot", "index.html"), "<html></html>");
            PathHelper.ContentPath = root.FullName;

            var app = WebApplication.CreateBuilder().Build();
            app.UseSpaUI();

            var fallback = ((IEndpointRouteBuilder)app).DataSources
                .SelectMany(s => s.Endpoints)
                .Single(e => e.Metadata.GetMetadata<SpaFallbackEndpointMetadata>() is not null);
            Assert.NotNull(fallback.Metadata.GetMetadata<IAllowAnonymous>());
        }
        finally
        {
            PathHelper.ContentPath = original;
            root.Delete(recursive: true);
        }
    }
}

/// <summary><see cref="PathHelper.ContentPath"/> is process-wide; tests that change it do
/// not run in parallel with each other.</summary>
[CollectionDefinition(nameof(PathHelperCollection), DisableParallelization = true)]
public class PathHelperCollection;
