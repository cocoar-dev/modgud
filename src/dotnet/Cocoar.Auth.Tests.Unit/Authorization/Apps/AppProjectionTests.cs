using Cocoar.Auth.Authorization.Apps;
using Cocoar.Auth.Authorization.Events;
using Cocoar.Auth.Authorization.Projections;

namespace Cocoar.Auth.Tests.Unit.Authorization.Apps;

/// <summary>
/// Pins the inline projection rules for <see cref="App"/>: every event MUST
/// land on exactly the field the projection intends to mutate. Drift here
/// breaks slug-uniqueness validation in the admin layer (which reads the
/// projected document synchronously).
/// </summary>
public class AppProjectionTests
{
    private static App NewState(
        string slug = "cocoar-auth",
        bool isSystem = true) =>
        new AppProjection().Create(new AppCreatedEvent(
            Id: Guid.NewGuid(),
            Slug: slug,
            DisplayName: "Cocoar.Auth",
            Description: "Identity provider",
            Resources: ["user", "session"],
            IsSystem: isSystem));

    public class Create
    {
        [Fact]
        public void Initialises_all_fields_from_event()
        {
            var id = Guid.NewGuid();
            var state = new AppProjection().Create(new AppCreatedEvent(
                Id: id,
                Slug: "timetodo",
                DisplayName: "TimeToDo",
                Description: "Task tracker",
                Resources: ["todo", "project"],
                IsSystem: false));

            Assert.Equal(id, state.Id);
            Assert.Equal("timetodo", state.Slug);
            Assert.Equal("TimeToDo", state.DisplayName);
            Assert.Equal("Task tracker", state.Description);
            Assert.Equal(new[] { "todo", "project" }, state.Resources);
            Assert.False(state.IsSystem);
            Assert.False(state.IsDeleted);
        }

        [Fact]
        public void Stores_resources_in_a_mutable_list_copy()
        {
            // Apply* later replaces Resources via [.. event.Resources]; the initial
            // list MUST also be a copy so mutating the source doesn't poison
            // cached projection state.
            var sourceResources = new List<string> { "a", "b" };
            var state = new AppProjection().Create(new AppCreatedEvent(
                Guid.NewGuid(), "app", "App", null, sourceResources, IsSystem: false));

            sourceResources[0] = "MUTATED";

            Assert.Equal("a", state.Resources[0]);
        }

        [Fact]
        public void Description_supports_null()
        {
            var state = new AppProjection().Create(new AppCreatedEvent(
                Guid.NewGuid(), "app", "App", Description: null, [], IsSystem: false));

            Assert.Null(state.Description);
        }

        [Fact]
        public void IsSystem_flag_is_carried_through()
        {
            var state = new AppProjection().Create(new AppCreatedEvent(
                Guid.NewGuid(), "cocoar-auth", "Cocoar.Auth", null, [], IsSystem: true));

            Assert.True(state.IsSystem);
        }
    }

    public class Apply
    {
        [Fact]
        public void Updated_event_replaces_displayname_description_and_resources()
        {
            var p = new AppProjection();
            var s = NewState();

            p.Apply(new AppUpdatedEvent(
                s.Id,
                DisplayName: "New Display",
                Description: "New desc",
                Resources: ["x", "y", "z"]), s);

            Assert.Equal("New Display", s.DisplayName);
            Assert.Equal("New desc", s.Description);
            Assert.Equal(new[] { "x", "y", "z" }, s.Resources);
        }

        [Fact]
        public void Updated_event_does_not_change_slug_or_IsSystem()
        {
            var p = new AppProjection();
            var s = NewState(slug: "cocoar-auth", isSystem: true);

            p.Apply(new AppUpdatedEvent(
                s.Id, "Renamed", null, []), s);

            // Slug and IsSystem are immutable after creation — Updated has no
            // fields for them, so they must survive an update.
            Assert.Equal("cocoar-auth", s.Slug);
            Assert.True(s.IsSystem);
        }

        [Fact]
        public void Updated_event_can_set_description_to_null()
        {
            var p = new AppProjection();
            var s = NewState();

            p.Apply(new AppUpdatedEvent(s.Id, s.DisplayName, null, s.Resources), s);

            Assert.Null(s.Description);
        }

        [Fact]
        public void Updated_event_makes_a_mutable_copy_of_resources()
        {
            var p = new AppProjection();
            var s = NewState();
            var sourceResources = new List<string> { "a", "b" };

            p.Apply(new AppUpdatedEvent(s.Id, s.DisplayName, s.Description, sourceResources), s);
            sourceResources[0] = "MUTATED";

            Assert.Equal("a", s.Resources[0]);
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

            var s = p.Create(new AppCreatedEvent(
                id, "timetodo", "TimeToDo", "old desc", ["todo"], IsSystem: false));
            p.Apply(new AppUpdatedEvent(id, "TimeToDo (renamed)", "new desc", ["todo", "project"]), s);

            Assert.Equal(id, s.Id);
            Assert.Equal("timetodo", s.Slug);
            Assert.Equal("TimeToDo (renamed)", s.DisplayName);
            Assert.Equal("new desc", s.Description);
            Assert.Equal(2, s.Resources.Count);
            Assert.False(s.IsSystem);
            Assert.False(s.IsDeleted);
        }

        [Fact]
        public void Delete_after_updates_sets_only_IsDeleted()
        {
            var id = Guid.NewGuid();
            var p = new AppProjection();

            var s = p.Create(new AppCreatedEvent(
                id, "timetodo", "TimeToDo", null, ["todo"], IsSystem: false));
            p.Apply(new AppUpdatedEvent(id, "TimeToDo!", null, ["todo", "project"]), s);
            p.Apply(new AppDeletedEvent(id), s);

            Assert.True(s.IsDeleted);
            // Updates before delete are still visible — soft-delete preserves history.
            Assert.Equal("TimeToDo!", s.DisplayName);
            Assert.Equal(2, s.Resources.Count);
        }
    }
}
