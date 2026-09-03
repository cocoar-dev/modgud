using Modgud.Authorization.Apps;
using Modgud.Authorization.Events;
using Modgud.Authorization.Projections;

namespace Modgud.Tests.Unit.Authorization.Apps;

/// <summary>
/// Pins the inline projection rules for <see cref="App"/>: every event MUST
/// land on exactly the field the projection intends to mutate. Drift here
/// breaks slug-uniqueness validation in the admin layer (which reads the
/// projected document synchronously).
/// </summary>
public class AppProjectionTests
{
    private static AppPermission Perm(string resource, string action) =>
        new(Guid.NewGuid(), resource, action, Description: null);

    private static App NewState(
        string slug = "modgud",
        bool isSystem = true) =>
        new AppProjection().ApplyCreated(new AppCreatedEvent(
            Id: Guid.NewGuid(),
            Slug: slug,
            DisplayName: "Modgud",
            Description: "Identity provider",
            Permissions: [Perm("user", "read"), Perm("session", "read")],
            IsSystem: isSystem));

    public class Create
    {
        [Fact]
        public void Initialises_all_fields_from_event()
        {
            var id = Guid.NewGuid();
            var todoRead = Perm("todo", "read");
            var projectRead = Perm("project", "read");
            var state = new AppProjection().ApplyCreated(new AppCreatedEvent(
                Id: id,
                Slug: "acme-tasks",
                DisplayName: "Acme Tasks",
                Description: "Task tracker",
                Permissions: [todoRead, projectRead],
                IsSystem: false));

            Assert.Equal(id, state.Id);
            Assert.Equal("acme-tasks", state.Slug);
            Assert.Equal("Acme Tasks", state.DisplayName);
            Assert.Equal("Task tracker", state.Description);
            Assert.Equal(new[] { todoRead, projectRead }, state.Permissions);
            Assert.False(state.IsSystem);
            Assert.False(state.IsDeleted);
        }

        [Fact]
        public void Stores_permissions_in_a_mutable_list_copy()
        {
            // Apply* later replaces Permissions via [.. event.Permissions]; the
            // initial list MUST also be a copy so mutating the source doesn't
            // poison cached projection state.
            var a = Perm("a", "read");
            var b = Perm("b", "read");
            var sourcePermissions = new List<AppPermission> { a, b };
            var state = new AppProjection().ApplyCreated(new AppCreatedEvent(
                Guid.NewGuid(), "app", "App", null, sourcePermissions, IsSystem: false));

            sourcePermissions[0] = Perm("MUTATED", "read");

            Assert.Equal(a, state.Permissions[0]);
        }

        [Fact]
        public void Description_supports_null()
        {
            var state = new AppProjection().ApplyCreated(new AppCreatedEvent(
                Guid.NewGuid(), "app", "App", Description: null, [], IsSystem: false));

            Assert.Null(state.Description);
        }

        [Fact]
        public void IsSystem_flag_is_carried_through()
        {
            var state = new AppProjection().ApplyCreated(new AppCreatedEvent(
                Guid.NewGuid(), "modgud", "Modgud", null, [], IsSystem: true));

            Assert.True(state.IsSystem);
        }
    }

    public class Apply
    {
        [Fact]
        public void Updated_event_replaces_displayname_description_and_permissions()
        {
            var p = new AppProjection();
            var s = NewState();
            var newPerms = new List<AppPermission>
            {
                Perm("x", "read"), Perm("y", "read"), Perm("z", "read"),
            };

            p.Apply(new AppUpdatedEvent(
                s.Id,
                DisplayName: "New Display",
                Description: "New desc",
                Permissions: newPerms), s);

            Assert.Equal("New Display", s.DisplayName);
            Assert.Equal("New desc", s.Description);
            Assert.Equal(newPerms, s.Permissions);
        }

        [Fact]
        public void Updated_event_does_not_change_slug_or_IsSystem()
        {
            var p = new AppProjection();
            var s = NewState(slug: "modgud", isSystem: true);

            p.Apply(new AppUpdatedEvent(
                s.Id, "Renamed", null, []), s);

            // Slug and IsSystem are immutable after creation — Updated has no
            // fields for them, so they must survive an update.
            Assert.Equal("modgud", s.Slug);
            Assert.True(s.IsSystem);
        }

        [Fact]
        public void Updated_event_can_set_description_to_null()
        {
            var p = new AppProjection();
            var s = NewState();

            p.Apply(new AppUpdatedEvent(s.Id, s.DisplayName, null, s.Permissions), s);

            Assert.Null(s.Description);
        }

        [Fact]
        public void Updated_event_makes_a_mutable_copy_of_permissions()
        {
            var p = new AppProjection();
            var s = NewState();
            var a = Perm("a", "read");
            var sourcePermissions = new List<AppPermission> { a, Perm("b", "read") };

            p.Apply(new AppUpdatedEvent(s.Id, s.DisplayName, s.Description, sourcePermissions), s);
            sourcePermissions[0] = Perm("MUTATED", "read");

            Assert.Equal(a, s.Permissions[0]);
        }

        [Fact]
        public void Deleted_sets_IsDeleted_flag()
        {
            var p = new AppProjection();
            var s = NewState();

            p.Apply(new AppDeletedEvent(s.Id), s);

            Assert.True(s.IsDeleted);
        }
    }

    public class EventReplay
    {
        [Fact]
        public void Full_lifecycle_replays_to_final_state()
        {
            var id = Guid.NewGuid();
            var p = new AppProjection();

            var s = p.ApplyCreated(new AppCreatedEvent(
                id, "acme-tasks", "Acme Tasks", "old desc",
                [Perm("todo", "read")], IsSystem: false));
            p.Apply(new AppUpdatedEvent(id, "Acme Tasks (renamed)", "new desc",
                [Perm("todo", "read"), Perm("project", "read")]), s);

            Assert.Equal(id, s.Id);
            Assert.Equal("acme-tasks", s.Slug);
            Assert.Equal("Acme Tasks (renamed)", s.DisplayName);
            Assert.Equal("new desc", s.Description);
            Assert.Equal(2, s.Permissions.Count);
            Assert.False(s.IsSystem);
            Assert.False(s.IsDeleted);
        }

        [Fact]
        public void Delete_after_updates_sets_only_IsDeleted()
        {
            var id = Guid.NewGuid();
            var p = new AppProjection();

            var s = p.ApplyCreated(new AppCreatedEvent(
                id, "acme-tasks", "Acme Tasks", null,
                [Perm("todo", "read")], IsSystem: false));
            p.Apply(new AppUpdatedEvent(id, "Acme Tasks!", null,
                [Perm("todo", "read"), Perm("project", "read")]), s);
            p.Apply(new AppDeletedEvent(id), s);

            Assert.True(s.IsDeleted);
            // Updates before delete are still visible — soft-delete preserves history.
            Assert.Equal("Acme Tasks!", s.DisplayName);
            Assert.Equal(2, s.Permissions.Count);
        }
    }
}
