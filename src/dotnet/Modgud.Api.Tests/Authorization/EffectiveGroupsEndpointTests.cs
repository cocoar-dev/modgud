using System.Net;
using System.Net.Http.Json;
using BuildingBlocks.Helper;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Authorization.Apps;
using Modgud.Authorization.Membership;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace Modgud.Api.Tests.Authorization;

/// <summary>
/// Integration tests for <c>GET /api/user/{id}/effective-groups</c> — the admin
/// debug surface that resolves live effective group membership independent of
/// whether <see cref="Group.MemberIds"/> is materialized.
///
/// <para>Three pinning shapes:
/// <list type="bullet">
///   <item>Manual-only: a directly-listed user surfaces with Source=DirectManual.</item>
///   <item>Auto-script match: even when <see cref="Group.MemberIds"/> is empty,
///         a passing predicate surfaces the group with Source=AutoMatched and
///         <c>MaterializedMatches=false</c> — the gold debug signal.</item>
///   <item>Broken script: returns a Diagnostic row instead of crashing.</item>
/// </list></para>
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class EffectiveGroupsEndpointTests : IntegrationTestBase
{
    public EffectiveGroupsEndpointTests(SharedPostgresFixture fixture) : base(fixture) { }

    [Fact]
    public async Task ManualMembership_Surfaces_As_DirectManual()
    {
        // Arrange — user listed directly in MemberIds of a Manual group.
        var user = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Manual", lastname: "Only", acronym: "mo",
            email: "mo@test.com", password: "TestPass1234",
            isRealmAdmin: true);
        var manualGroup = await Factory.CreateTestGroupAsync(
            name: $"ManualGroup_{Guid.NewGuid():N}",
            memberIds: [user.Id],
            roleIds: []);

        // Act
        var resp = await Client.GetAsync(
            $"/api/user/{new ShortGuid(user.Id)}/effective-groups",
            TestContext.Current.CancellationToken);

        // Assert
        resp.EnsureSuccessStatusCode();
        var result = await resp.Content.ReadFromJsonAsync<EffectiveGroupsTestResponse>(
            JsonOptions, TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        var row = Assert.Single(result!.Groups, g => g.Id == new ShortGuid(manualGroup.Id).ToString());
        Assert.Equal("DirectManual", row.Source);
        Assert.Null(row.MaterializedMatches);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public async Task AutoScript_Match_With_Drift_Returns_AutoMatched_With_MaterializedMatches_False()
    {
        // Arrange — a person whose email ends with @demo.local, plus an Auto group
        // whose script matches that pattern but whose MemberIds is intentionally
        // left empty (simulating "the recompute never ran" — the anna.bauer bug shape).
        var user = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Anna", lastname: "Bauer", acronym: "ab",
            email: "anna.bauer@demo.local", password: "TestPass1234",
            isRealmAdmin: true);

        // Compile the same script the demo seed uses.
        const string script = "(p) => Type.Is(p, 'person') && p.IsActive && (p.Email != null) && p.Email.endsWith('@demo.local')";
        using var scope = Factory.Services.CreateScope();
        var evaluator = scope.ServiceProvider.GetRequiredService<IMembershipEvaluator>();
        var compiled = evaluator.TranspileMembershipScript(script);

        // Persist an Auto group with the predicate but EMPTY MemberIds.
        var autoGroup = new Group
        {
            Id = Guid.CreateVersion7(),
            Name = $"InternalStaffAuto_{Guid.NewGuid():N}",
            Description = "Auto-membership for @demo.local",
            MemberIds = [], // intentionally stale
            RoleIds = [],
            BoundTo = [AppSlugs.Modgud],
            MembershipMode = MembershipMode.Auto,
            MembershipScript = script,
            CompiledMembershipScript = compiled,
        };
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        // PrincipalProjection (inline) builds the Group doc from
        // GroupCreatedEvent — direct Store conflicts under Marten 8.34+.
        session.Events.StartStream(autoGroup.Id,
            new GroupCreatedEvent(
                autoGroup.Id, autoGroup.Name, autoGroup.Description,
                autoGroup.MemberIds, autoGroup.RoleIds,
                autoGroup.MembershipMode, autoGroup.MembershipScript, autoGroup.CompiledMembershipScript,
                autoGroup.MembershipScriptDependencies,
                autoGroup.Email, autoGroup.EmailMode,
                autoGroup.BoundTo));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Act
        var resp = await Client.GetAsync(
            $"/api/user/{new ShortGuid(user.Id)}/effective-groups",
            TestContext.Current.CancellationToken);

        // Assert
        resp.EnsureSuccessStatusCode();
        var result = await resp.Content.ReadFromJsonAsync<EffectiveGroupsTestResponse>(
            JsonOptions, TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        var row = Assert.Single(result!.Groups, g => g.Id == new ShortGuid(autoGroup.Id).ToString());
        Assert.Equal("AutoMatched", row.Source);
        // Drift signal: predicate matches but MemberIds is empty.
        Assert.False(row.MaterializedMatches);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public async Task BrokenScript_Returns_Diagnostic_And_No_Crash()
    {
        // Arrange — a user and an Auto group whose compiled script references a
        // field that doesn't exist on Principal (NullReferenceException at eval).
        var user = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Broke", lastname: "Script", acronym: "bs",
            email: "bs@test.com", password: "TestPass1234",
            isRealmAdmin: true);

        // The script reads a property of a property of an undefined field. Compiling
        // succeeds (the JsLinq translator accepts the shape), but evaluating it
        // against a Principal whose `BogusField` is null throws — that's the path
        // the resolver must catch and surface as Diagnostic, not crash.
        const string script = "(p) => p.BogusField.Inner === 'x'";
        using var scope = Factory.Services.CreateScope();
        var evaluator = scope.ServiceProvider.GetRequiredService<IMembershipEvaluator>();
        string? compiled = null;
        try
        {
            compiled = evaluator.TranspileMembershipScript(script);
        }
        catch
        {
            // If transpile itself fails, the script never lands in CompiledMembershipScript;
            // pin a known-bad-but-syntactically-valid alternative below.
        }

        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var autoGroup = new Group
        {
            Id = Guid.CreateVersion7(),
            Name = $"BrokenAuto_{Guid.NewGuid():N}",
            Description = "Will fail at evaluate",
            MemberIds = [],
            RoleIds = [],
            BoundTo = [AppSlugs.Modgud],
            MembershipMode = MembershipMode.Auto,
            MembershipScript = script,
            // Force an unmistakably broken compiled script so the build/eval phase
            // throws inside the resolver — the test passes whether the failure
            // surfaces as CompileFailed or EvalFailed (both are Diagnostic outcomes).
            CompiledMembershipScript = compiled ?? "this is not a valid arrow function",
        };
        // PrincipalProjection (inline) builds the Group doc from
        // GroupCreatedEvent — direct Store conflicts under Marten 8.34+.
        session.Events.StartStream(autoGroup.Id,
            new GroupCreatedEvent(
                autoGroup.Id, autoGroup.Name, autoGroup.Description,
                autoGroup.MemberIds, autoGroup.RoleIds,
                autoGroup.MembershipMode, autoGroup.MembershipScript, autoGroup.CompiledMembershipScript,
                autoGroup.MembershipScriptDependencies,
                autoGroup.Email, autoGroup.EmailMode,
                autoGroup.BoundTo));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Act
        var resp = await Client.GetAsync(
            $"/api/user/{new ShortGuid(user.Id)}/effective-groups",
            TestContext.Current.CancellationToken);

        // Assert — endpoint did NOT crash, and the broken group surfaces as a
        // diagnostic row rather than a normal Group entry.
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var result = await resp.Content.ReadFromJsonAsync<EffectiveGroupsTestResponse>(
            JsonOptions, TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        Assert.DoesNotContain(result!.Groups, g => g.Id == new ShortGuid(autoGroup.Id).ToString());
        var diag = Assert.Single(result.Diagnostics, d => d.GroupId == new ShortGuid(autoGroup.Id).ToString());
        Assert.True(diag.Kind is "EvalFailed" or "CompileFailed");
        Assert.False(string.IsNullOrWhiteSpace(diag.Error));
    }

    // ── DTOs (test-local; pinning only the wire shape we care about) ─────────

    private sealed record EffectiveGroupsTestResponse(
        string PrincipalId,
        List<EffectiveGroupTestRow> Groups,
        List<DiagnosticTestRow> Diagnostics);

    private sealed record EffectiveGroupTestRow(
        string Id,
        string Name,
        string? Description,
        string Source,
        List<ViaStepTestRow>? Via,
        bool? MaterializedMatches);

    private sealed record ViaStepTestRow(string Id, string Name);

    private sealed record DiagnosticTestRow(string GroupId, string GroupName, string Kind, string Error);
}
