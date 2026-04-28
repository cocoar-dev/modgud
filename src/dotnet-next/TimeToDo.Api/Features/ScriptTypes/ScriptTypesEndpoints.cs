using TimeToDo.Authorization.Access;
using TimeToDo.Authorization.Principals;
using Cocoar.JsEval.TsDefinition;
using TimeToDo.Authentication.Domain;
using TimeToDo.Infrastructure.Persistence.Marten.Projections.Customers;

using TimeToDo.Infrastructure.Persistence.Marten.Projections.Todos;

namespace TimeToDo.Api.Features.ScriptTypes;

/// <summary>
/// Generates TypeScript <c>.d.ts</c> definitions for the Monaco script editor from
/// live sources — domain types via <see cref="DefinitionBuilder"/> (C# reflection,
/// flattened to short names via <c>MapNamespace</c>) and module types via
/// <see cref="TsDefinitionService"/> (picks up <c>linq.d.ts</c> automatically once
/// <c>AddLinq()</c> is registered). The only hand-written line is the runtime
/// binding <c>declare const user</c> — no type-alias block, no name drift.
/// </summary>
public static class ScriptTypesEndpoints
{
    public static WebApplication MapScriptTypesEndpoints(this WebApplication application, string path)
    {
        var group = application.MapGroup($"{path}/script-types")
            .WithTags("Script Types")
            .RequireAuthorization();

        group.MapGet("principal", (TsDefinitionService definitionService) =>
        {
            var builder = new DefinitionBuilder()
                .MapNamespace("TimeToDo", "");

            builder.AddTypes(
                typeof(Person),
                typeof(Group),
                typeof(TodoView),
                typeof(CustomerView),
                typeof(UserContext));

            var domainFiles = builder.Render();
            var moduleFiles = definitionService.GetTsDefinitions();

            // `user` and `env` are set via SetValue before each script execution.
            // `linq` is declared by the Linq module's own .d.ts contributor.
            // Resource-specific globals (todos/customers, setResult) live in the per-resource
            //   preamble on the frontend so setResult is strongly typed per resource type.
            const string globals = """
                declare const user: UserContext;
                declare const env: { AllowedCustomerIds(): Promise<readonly string[]> };
                declare interface IQueryable<T> {
                  where(predicate: (item: T) => boolean): IQueryable<T>;
                }
                """;

            var combined = string.Join("\n\n",
                moduleFiles.Values
                    .Concat(domainFiles.Values)
                    .Append(globals));

            return Results.Text(combined, "application/typescript; charset=utf-8");
        })
        .WithName("ScriptTypes_Principal");

        return application;
    }
}
