using Modgud.Authorization.Events;
using Modgud.Authorization.Membership;
using Modgud.Authorization.Principals;
using Cocoar.JsEval.TypeScript;
using ErrorOr;
using Marten;

namespace Modgud.Authorization.Commands;

public record CreateGroupCommand(
    string Name,
    string? Description,
    List<Guid> MemberIds,
    List<Guid> RoleIds,
    MembershipMode MembershipMode = MembershipMode.Manual,
    string? MembershipScript = null,
    string? Email = null,
    EmailMode EmailMode = EmailMode.Shared,
    List<string>? BoundTo = null,
    bool ExternallyDrivable = false,
    // Whether the caller already holds realm:admin. Set by the endpoint from
    // the authenticated principal. Defaults false (fail-closed): a command
    // constructed without it cannot confer realm:admin.
    bool CallerIsRealmAdmin = false,
    // Optional pinned entity id — provisioning only (the manifest applier
    // pre-checks stream availability); server-generated when null.
    Guid? Id = null);

public class CreateGroupHandler(
    IDocumentSession session,
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

        // Federation v1 (decision G): realm:admin is hard local-only. A group
        // that confers realm:admin can never be externally drivable, because
        // external claims are untrusted input. Enforced bidirectionally at the
        // create/update seam where RoleIds and ExternallyDrivable are set together.
        if (command.ExternallyDrivable)
        {
            var guardError = await GroupMembershipGuards.RejectIfConfersRealmAdminAsync(
                session, command.RoleIds, ct);
            if (guardError is not null) return guardError.Value;
        }

        // Privilege-escalation guard (audit H1): only a realm:admin may create a
        // group that confers realm:admin. Without this, an authorization-group:write
        // holder could attach a realm-admin role to a group and self-escalate.
        if (!command.CallerIsRealmAdmin &&
            await GroupMembershipGuards.AnyRoleConfersRealmAdminAsync(session, command.RoleIds, ct))
        {
            return Error.Forbidden("Group.RealmAdminConferralForbidden",
                "Only a realm administrator may create a group that confers realm:admin.");
        }

        string? compiledMembership = null;
        List<string>? membershipDeps = null;
        if (command.MembershipMode == MembershipMode.Auto)
        {
            if (string.IsNullOrWhiteSpace(command.MembershipScript))
                return Error.Validation("Group.MembershipScriptRequired",
                    "MembershipScript is required when MembershipMode is Auto");
            // Cap script length AND nesting depth BEFORE handing the input
            // to the TS compiler. Closes Gap-2 (unbounded compiler work) and
            // the consumer-side window of F6b (interpreted TS parser blows
            // .NET stack on deeply-nested input). See JsEval threat model.
            var inputError = ScriptInputLimits.Validate(
                command.MembershipScript, "Group.MembershipScript");
            if (inputError is not null) return inputError.Value;
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
            Id = command.Id ?? Guid.NewGuid(),
            Name = command.Name,
            Description = command.Description,
            MemberIds = memberIds,
            RoleIds = command.RoleIds.ToList(),
            MembershipMode = command.MembershipMode,
            MembershipScript = command.MembershipScript,
            CompiledMembershipScript = compiledMembership,
            MembershipScriptDependencies = membershipDeps,
            Email = command.Email,
            EmailMode = command.EmailMode,
            BoundTo = command.BoundTo?.ToList() ?? [],
            ExternallyDrivable = command.ExternallyDrivable,
        };

        session.Events.StartStream(group.Id,
            new GroupCreatedEvent(group.Id, group.Name, group.Description,
                group.MemberIds, group.RoleIds,
                group.MembershipMode, group.MembershipScript, group.CompiledMembershipScript,
                group.MembershipScriptDependencies,
                group.Email, group.EmailMode,
                group.BoundTo, group.ExternallyDrivable));

        if (group.MembershipMode == MembershipMode.Auto)
            await recalculator.RecalculateForGroupAsync(group, session, ct);

        await session.SaveChangesAsync(ct);

        return (await session.LoadAsync<Group>(group.Id, ct))!;
    }

    private static string FormatTranspileErrors(TsTranspileException ex)
        => string.Join("\n", ex.Errors.Select(d =>
            $"Line {d.Line}, col {d.Column}: TS{d.Code}: {d.Message}"));
}
