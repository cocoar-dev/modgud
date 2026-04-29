using Cocoar.Auth.Authorization.Access;
using Cocoar.Auth.Authorization.Events;
using Cocoar.Auth.Authorization.Membership;
using Cocoar.Auth.Authorization.Principals;
using Cocoar.JsEval.TypeScript;
using ErrorOr;
using Marten;

namespace Cocoar.Auth.Authorization.Commands;

public record CreateGroupCommand(
    string Name,
    string? Description,
    List<Guid> MemberIds,
    List<Guid> RoleIds,
    List<AccessScriptInput> AccessScripts,
    MembershipMode MembershipMode = MembershipMode.Manual,
    string? MembershipScript = null,
    string? Email = null,
    EmailMode EmailMode = EmailMode.Shared);

public class CreateGroupHandler(
    IDocumentSession session,
    IAccessPolicyEngine accessPolicyEngine,
    IMembershipEvaluator membershipEvaluator,
    IAutoMembershipRecalculator recalculator)
{
    public async Task<ErrorOr<Group>> Handle(
        CreateGroupCommand command,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(command.Name))
            return Error.Validation("Group.NameRequired", "Group name is required.");

        var normalized = command.Name.Trim();
        var nameTaken = await session.Query<Group>()
            .Where(g => !g.IsDeleted && g.Name == normalized)
            .AnyAsync(ct);
        if (nameTaken)
            return Error.Conflict("Group.NameTaken",
                $"A group with the name '{normalized}' already exists.");

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
            // Dependency collection happens per concrete principal type inside the
            // recalculator. At create-time we don't have a TPrincipal here, so leave
            // deps null — the recalculator's initial RecalculateForGroupAsync emits
            // a corrective event if needed.
        }

        var memberIds = command.MembershipMode == MembershipMode.Auto
            ? new List<Guid>()
            : command.MemberIds.ToList();

        var group = new Group
        {
            Id = Guid.NewGuid(),
            Name = command.Name,
            Description = command.Description,
            MemberIds = memberIds,
            RoleIds = command.RoleIds.ToList(),
            AccessScripts = accessScripts,
            MembershipMode = command.MembershipMode,
            MembershipScript = command.MembershipScript,
            CompiledMembershipScript = compiledMembership,
            MembershipScriptDependencies = membershipDeps,
            Email = command.Email,
            EmailMode = command.EmailMode,
        };

        session.Events.StartStream(group.Id,
            new GroupCreatedEvent(group.Id, group.Name, group.Description,
                group.MemberIds, group.RoleIds, group.AccessScripts,
                group.MembershipMode, group.MembershipScript, group.CompiledMembershipScript,
                group.MembershipScriptDependencies,
                group.Email, group.EmailMode));

        if (group.MembershipMode == MembershipMode.Auto)
            await recalculator.RecalculateForGroupAsync(group, session, ct);

        await session.SaveChangesAsync(ct);

        return (await session.LoadAsync<Group>(group.Id, ct))!;
    }

    private static string FormatTranspileErrors(TsTranspileException ex)
        => string.Join("\n", ex.Errors.Select(d =>
            $"Line {d.Line}, col {d.Column}: TS{d.Code}: {d.Message}"));
}
