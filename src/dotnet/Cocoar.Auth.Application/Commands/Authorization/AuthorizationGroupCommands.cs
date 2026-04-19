using Cocoar.Auth.Application.Authorization;
using Cocoar.Auth.Application.DTOs.Authorization;
using Cocoar.Auth.Domain.Authorization;
using Cocoar.Auth.Domain.Authorization.Events;
using Cocoar.Auth.Domain.Principals;
using ErrorOr;
using Marten;
using Microsoft.Extensions.Logging;

namespace Cocoar.Auth.Application.Commands.Authorization;

// ── Create ────────────────────────────────────────────────────────────

public record CreateAuthorizationGroupCommand(CreateAuthorizationGroupInput Input);

public class CreateAuthorizationGroupHandler(
    IDocumentSession session,
    IMembershipEvaluator evaluator,
    ILogger<CreateAuthorizationGroupHandler> logger)
{
    public async Task<ErrorOr<AuthorizationGroupDto>> HandleAsync(
        CreateAuthorizationGroupCommand command, CancellationToken ct)
    {
        var input = command.Input;
        if (string.IsNullOrWhiteSpace(input.Name))
            return Error.Validation("AuthorizationGroup.Name", "Name is required.");

        var nameTaken = await session.Query<AuthorizationGroup>()
            .AnyAsync(g => !g.IsDeleted && g.Name == input.Name, ct);
        if (nameTaken)
            return Error.Conflict("AuthorizationGroup.Name", $"A group named '{input.Name}' already exists.");

        var compiled = ScriptCompilation.Compile(input.AccessScripts, input.MembershipScript, input.MembershipMode, evaluator, logger);
        var id = Guid.CreateVersion7();

        session.Events.StartStream<AuthorizationGroup>(id,
            new AuthorizationGroupCreatedEvent(
                Id: id,
                Name: input.Name,
                Description: input.Description,
                MemberIds: input.MemberIds ?? [],
                RoleIds: input.RoleIds ?? [],
                AccessScripts: compiled.AccessScripts,
                MembershipMode: input.MembershipMode,
                MembershipScript: input.MembershipScript,
                CompiledMembershipScript: compiled.CompiledMembershipScript,
                MembershipScriptDependencies: compiled.MembershipDependencies,
                Email: input.Email,
                EmailMode: input.EmailMode));
        await session.SaveChangesAsync(ct);

        return new AuthorizationGroupDto
        {
            Id = id,
            Name = input.Name,
            Description = input.Description,
            MemberIds = input.MemberIds ?? [],
            RoleIds = input.RoleIds ?? [],
            AccessScripts = input.AccessScripts ?? [],
            MembershipMode = input.MembershipMode,
            MembershipScript = input.MembershipScript,
            MembershipScriptDependencies = compiled.MembershipDependencies,
            Email = input.Email,
            EmailMode = input.EmailMode,
        };
    }
}

// ── Update ────────────────────────────────────────────────────────────

public record UpdateAuthorizationGroupCommand(Guid Id, UpdateAuthorizationGroupInput Input);

public class UpdateAuthorizationGroupHandler(
    IDocumentSession session,
    IMembershipEvaluator evaluator,
    ILogger<UpdateAuthorizationGroupHandler> logger)
{
    public async Task<ErrorOr<AuthorizationGroupDto>> HandleAsync(
        UpdateAuthorizationGroupCommand command, CancellationToken ct)
    {
        var current = await session.LoadAsync<AuthorizationGroup>(command.Id, ct);
        if (current is null || current.IsDeleted)
            return Error.NotFound("AuthorizationGroup.NotFound", $"AuthorizationGroup {command.Id} not found.");

        var input = command.Input;
        if (string.IsNullOrWhiteSpace(input.Name))
            return Error.Validation("AuthorizationGroup.Name", "Name is required.");

        var nameTaken = await session.Query<AuthorizationGroup>()
            .AnyAsync(g => !g.IsDeleted && g.Id != command.Id && g.Name == input.Name, ct);
        if (nameTaken)
            return Error.Conflict("AuthorizationGroup.Name", $"A group named '{input.Name}' already exists.");

        var compiled = ScriptCompilation.Compile(input.AccessScripts, input.MembershipScript, input.MembershipMode, evaluator, logger);

        session.Events.Append(command.Id,
            new AuthorizationGroupUpdatedEvent(
                Id: command.Id,
                Name: input.Name,
                Description: input.Description,
                MemberIds: input.MemberIds ?? [],
                RoleIds: input.RoleIds ?? [],
                AccessScripts: compiled.AccessScripts,
                MembershipMode: input.MembershipMode,
                MembershipScript: input.MembershipScript,
                CompiledMembershipScript: compiled.CompiledMembershipScript,
                MembershipScriptDependencies: compiled.MembershipDependencies,
                Email: input.Email,
                EmailMode: input.EmailMode));
        await session.SaveChangesAsync(ct);

        return new AuthorizationGroupDto
        {
            Id = command.Id,
            Name = input.Name,
            Description = input.Description,
            MemberIds = input.MemberIds ?? [],
            RoleIds = input.RoleIds ?? [],
            AccessScripts = input.AccessScripts ?? [],
            MembershipMode = input.MembershipMode,
            MembershipScript = input.MembershipScript,
            MembershipScriptDependencies = compiled.MembershipDependencies,
            Email = input.Email,
            EmailMode = input.EmailMode,
        };
    }
}

// ── Delete ────────────────────────────────────────────────────────────

public record DeleteAuthorizationGroupCommand(Guid Id);

public class DeleteAuthorizationGroupHandler(IDocumentSession session)
{
    public async Task<ErrorOr<Deleted>> HandleAsync(
        DeleteAuthorizationGroupCommand command, CancellationToken ct)
    {
        var current = await session.LoadAsync<AuthorizationGroup>(command.Id, ct);
        if (current is null || current.IsDeleted)
            return Error.NotFound("AuthorizationGroup.NotFound", $"AuthorizationGroup {command.Id} not found.");

        session.Events.Append(command.Id, new AuthorizationGroupDeletedEvent(command.Id));
        await session.SaveChangesAsync(ct);
        return Result.Deleted;
    }
}

// ── Shared compile helper ─────────────────────────────────────────────

internal record CompiledScripts(
    List<ResourceAccessScript> AccessScripts,
    string? CompiledMembershipScript,
    List<string>? MembershipDependencies);

internal static class ScriptCompilation
{
    public static CompiledScripts Compile(
        List<ResourceAccessScriptDto>? accessScripts,
        string? membershipScript,
        MembershipMode mode,
        IMembershipEvaluator evaluator,
        ILogger logger)
    {
        var compiledAccess = (accessScripts ?? [])
            .Select(s => new ResourceAccessScript
            {
                ResourceType = s.ResourceType,
                Script = s.Script,
                CompiledScript = string.IsNullOrWhiteSpace(s.Script) ? null : SafeTranspile(s.Script, evaluator, logger),
            })
            .ToList();

        string? compiledMembership = null;
        List<string>? deps = null;
        if (mode == MembershipMode.Auto && !string.IsNullOrWhiteSpace(membershipScript))
        {
            compiledMembership = SafeTranspile(membershipScript!, evaluator, logger);
            if (compiledMembership is not null)
            {
                try
                {
                    var collected = evaluator.CollectDependencies<IPrincipal>(compiledMembership);
                    deps = collected?.ToList();
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to collect membership-script dependencies; treating as invalidate-all.");
                    deps = null;
                }
            }
        }

        return new CompiledScripts(compiledAccess, compiledMembership, deps);
    }

    private static string? SafeTranspile(string script, IMembershipEvaluator evaluator, ILogger logger)
    {
        try
        {
            return evaluator.TranspileMembershipScript(script);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Script transpile failed; storing raw source only.");
            return null;
        }
    }
}
