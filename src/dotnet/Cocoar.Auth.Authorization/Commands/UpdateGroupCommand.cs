using Cocoar.Auth.Authorization.Access;
using Cocoar.Auth.Authorization.Events;
using Cocoar.Auth.Authorization.Membership;
using Cocoar.Auth.Authorization.Principals;
using Cocoar.Auth.Authorization.Services;
using Cocoar.JsEval.TypeScript;
using ErrorOr;
using Marten;

namespace Cocoar.Auth.Authorization.Commands;

public record UpdateGroupCommand(
    Guid Id,
    string Name,
    string? Description,
    List<Guid> MemberIds,
    List<Guid> RoleIds,
    List<AccessScriptInput> AccessScripts,
    MembershipMode MembershipMode = MembershipMode.Manual,
    string? MembershipScript = null,
    string? Email = null,
    EmailMode EmailMode = EmailMode.Shared);

public class UpdateGroupHandler(
    IDocumentSession session,
    IAccessPolicyEngine accessPolicyEngine,
    IMembershipEvaluator membershipEvaluator,
    IPermissionService permissionService,
    IAutoMembershipRecalculator recalculator)
{
    public async Task<ErrorOr<Group>> Handle(
        UpdateGroupCommand command,
        CancellationToken ct)
    {
        var group = await session.LoadAsync<Group>(command.Id, ct);
        if (group is null || group.IsDeleted)
            return Error.NotFound("Group.NotFound", "Group not found");

        if (string.IsNullOrWhiteSpace(command.Name))
            return Error.Validation("Group.NameRequired", "Group name is required.");

        var normalized = command.Name.Trim();
        if (!string.Equals(normalized, group.Name, StringComparison.Ordinal))
        {
            var nameTaken = await session.Query<Group>()
                .Where(g => !g.IsDeleted && g.Id != command.Id && g.Name == normalized)
                .AnyAsync(ct);
            if (nameTaken)
                return Error.Conflict("Group.NameTaken",
                    $"A group with the name '{normalized}' already exists.");
        }

        // Cycle-detection: if any new member id is one of this group's descendants
        // (transitively), adding it would create a cycle.
        if (command.MembershipMode == MembershipMode.Manual && command.MemberIds.Count > 0)
        {
            var descendants = await permissionService.GetDescendantGroupIdsAsync(command.Id, ct);

            if (command.MemberIds.Contains(command.Id))
                return Error.Validation("Group.SelfMembership",
                    "A group cannot be its own member.");

            var cycleMembers = command.MemberIds.Where(id => descendants.Contains(id)).ToList();
            if (cycleMembers.Count > 0)
                return Error.Validation("Group.Cycle",
                    $"Adding group {cycleMembers[0]} as a member would create a cycle.");
        }

        var accessScripts = new List<ResourceAccessScript>();
        foreach (var input in command.AccessScripts)
        {
            string? compiled = null;
            if (!string.IsNullOrWhiteSpace(input.Script))
            {
                try { compiled = accessPolicyEngine.TranspileTypeScript(input.Script); }
                catch (TsTranspileException ex)
                {
                    return Error.Validation("Group.AccessScriptTranspile",
                        $"Access script ({input.ResourceType}): {FormatTranspileErrors(ex)}");
                }
                catch (Exception ex)
                {
                    return Error.Validation("Group.AccessScriptTranspile",
                        $"Transpile failed for {input.ResourceType}: {ex.Message}");
                }
            }
            accessScripts.Add(new ResourceAccessScript
            {
                ResourceType = input.ResourceType,
                Script = input.Script,
                CompiledScript = compiled,
            });
        }

        string? compiledMembership = null;
        List<string>? membershipDeps = null;
        if (command.MembershipMode == MembershipMode.Auto)
        {
            if (string.IsNullOrWhiteSpace(command.MembershipScript))
                return Error.Validation("Group.MembershipScriptRequired",
                    "MembershipScript is required when MembershipMode is Auto");
            try { compiledMembership = membershipEvaluator.TranspileMembershipScript(command.MembershipScript); }
            catch (TsTranspileException ex)
            {
                return Error.Validation("Group.MembershipScriptTranspile",
                    $"Membership script: {FormatTranspileErrors(ex)}");
            }
            catch (Exception ex)
            {
                return Error.Validation("Group.MembershipScriptTranspile",
                    $"Membership script transpile failed: {ex.Message}");
            }
            // Dependency collection needs TPrincipal — happens inside the
            // recalculator's initial RecalculateForGroupAsync right below.
        }

        var memberIds = command.MembershipMode == MembershipMode.Auto
            ? group.MemberIds
            : command.MemberIds.ToList();

        session.Events.Append(command.Id, new GroupUpdatedEvent(
            command.Id, command.Name, command.Description,
            memberIds, command.RoleIds.ToList(), accessScripts,
            command.MembershipMode, command.MembershipScript, compiledMembership,
            membershipDeps,
            command.Email, command.EmailMode));

        if (command.MembershipMode == MembershipMode.Auto)
        {
            var updatedGroup = new Group
            {
                Id = command.Id,
                Name = command.Name,
                Description = command.Description,
                MemberIds = memberIds,
                RoleIds = command.RoleIds.ToList(),
                AccessScripts = accessScripts,
                MembershipMode = command.MembershipMode,
                MembershipScript = command.MembershipScript,
                MembershipScriptDependencies = membershipDeps,
                CompiledMembershipScript = compiledMembership,
                Email = command.Email,
                EmailMode = command.EmailMode,
            };
            await recalculator.RecalculateForGroupAsync(updatedGroup, session, ct);
        }

        await session.SaveChangesAsync(ct);

        return (await session.LoadAsync<Group>(command.Id, ct))!;
    }

    private static string FormatTranspileErrors(TsTranspileException ex)
        => string.Join("\n", ex.Errors.Select(d =>
            $"Line {d.Line}, col {d.Column}: TS{d.Code}: {d.Message}"));
}
