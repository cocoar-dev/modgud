using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Projections;
using Marten;
using Marten.Events;using Marten.Events.Aggregation;
using Marten.Events.Projections;
using Testcontainers.PostgreSql;
using Weasel.Core;

Console.WriteLine("=== Marten Projection Lab ===\n");

// Start PostgreSQL container
Console.WriteLine("Starting PostgreSQL container...");
var postgres = new PostgreSqlBuilder()
    .WithImage("postgres:16-alpine")
    .Build();

await postgres.StartAsync();
var connectionString = postgres.GetConnectionString();
Console.WriteLine($"PostgreSQL started: {connectionString}\n");

try
{
    // ========================================
    // Test 1: SingleStreamProjection (stream-based aggregate)
    // ========================================
    Console.WriteLine("=== Test 1: SingleStreamProjection ===\n");
    await TestSingleStreamProjection(connectionString);

    // ========================================
    // Test 2: EventProjection (event-based, flat projection)
    // ========================================
    Console.WriteLine("\n=== Test 2: EventProjection ===\n");
    await TestEventProjection(connectionString);

    // ========================================
    // Test 3: SingleStreamProjection with different document type
    // ========================================
    Console.WriteLine("\n=== Test 3: SingleStreamProjection with Custom Read Model ===\n");
    await TestSingleStreamProjectionWithCustomReadModel(connectionString);

    // ========================================
    // Test 4: EventProjection with typed stream (mimicking production setup)
    // ========================================
    Console.WriteLine("\n=== Test 4: EventProjection with Typed Stream ===\n");
    await TestEventProjectionWithTypedStream(connectionString);

    // ========================================
    // Test 5: EventProjection with additional documents stored (mimicking production exactly)
    // ========================================
    Console.WriteLine("\n=== Test 5: EventProjection with Additional Documents ===\n");
    await TestEventProjectionWithAdditionalDocuments(connectionString);
}
finally
{
    await postgres.DisposeAsync();
    Console.WriteLine("\nPostgreSQL container stopped.");
}

// ============================================================================
// Test 5: Mimic production exactly - stream + additional documents
// ============================================================================
static async Task TestEventProjectionWithAdditionalDocuments(string connectionString)
{
    var store = DocumentStore.For(opts =>
    {
        opts.Connection(connectionString);
        opts.DatabaseSchemaName = "test5";
        opts.AutoCreateSchemaObjects = AutoCreate.All;

        // Register events
        opts.Events.AddEventType<PersonCreated>();
        opts.Events.AddEventType<PersonNameChanged>();

        // EventProjection
        opts.Projections.Add<PersonReadModelProjection>(ProjectionLifecycle.Inline);
    });

    await using var session = store.LightweightSession();

    var personId = Guid.NewGuid();
    Console.WriteLine($"Creating person with ID: {personId}");

    // This mimics exactly what EventSourcedUserStore does:
    // 1. Start stream with typed aggregate
    session.Events.StartStream<PersonAggregate>(personId, new PersonCreated(personId, "WithDocs", "Test"));

    // 2. Store additional documents (like UserSecurityData and ApplicationUser)
    session.Store(new PersonAggregate { Id = personId, FirstName = "WithDocs", LastName = "Test" });

    // 3. Save everything
    await session.SaveChangesAsync();
    Console.WriteLine("  -> Appended PersonCreated event + stored PersonAggregate document");

    // Query the read model
    var readModel = await session.LoadAsync<PersonReadModel>(personId);
    Console.WriteLine($"  -> Loaded PersonReadModel: {readModel?.FirstName} {readModel?.LastName}");

    if (readModel is null)
    {
        Console.WriteLine("  -> WARNING: PersonReadModel is NULL! EventProjection did not create it.");
    }
    else
    {
        Console.WriteLine("  -> SUCCESS: EventProjection created PersonReadModel correctly!");
    }
}

// ============================================================================
// Test 4: EventProjection with typed stream (like production UserAggregate -> UserReadModel)
// ============================================================================
static async Task TestEventProjectionWithTypedStream(string connectionString)
{
    var store = DocumentStore.For(opts =>
    {
        opts.Connection(connectionString);
        opts.DatabaseSchemaName = "test4";
        opts.AutoCreateSchemaObjects = AutoCreate.All;

        // Register events
        opts.Events.AddEventType<PersonCreated>();
        opts.Events.AddEventType<PersonNameChanged>();

        // EventProjection: manually create and update documents
        opts.Projections.Add<PersonReadModelProjection>(ProjectionLifecycle.Inline);
    });

    await using var session = store.LightweightSession();

    var personId = Guid.NewGuid();
    Console.WriteLine($"Creating person with ID: {personId}");

    // Start stream WITH A TYPED AGGREGATE (like production code does)
    session.Events.StartStream<PersonAggregate>(personId, new PersonCreated(personId, "TypedStream", "Person"));
    await session.SaveChangesAsync();
    Console.WriteLine("  -> Appended PersonCreated event (with typed stream)");

    // Query the read model
    var readModel = await session.LoadAsync<PersonReadModel>(personId);
    Console.WriteLine($"  -> Loaded PersonReadModel: {readModel?.FirstName} {readModel?.LastName}");

    if (readModel is null)
    {
        Console.WriteLine("  -> WARNING: PersonReadModel is NULL! EventProjection did not create it.");
    }
}

// ============================================================================
// Test 1: Standard SingleStreamProjection where the projection IS the aggregate
// ============================================================================
static async Task TestSingleStreamProjection(string connectionString)
{
    var store = DocumentStore.For(opts =>
    {
        opts.Connection(connectionString);
        opts.DatabaseSchemaName = "test1";
        opts.AutoCreateSchemaObjects = AutoCreate.All;

        // Register events
        opts.Events.AddEventType<PersonCreated>();
        opts.Events.AddEventType<PersonNameChanged>();

        // SingleStreamProjection: PersonAggregate is both the aggregate AND the read model
        opts.Projections.Snapshot<PersonAggregate>(SnapshotLifecycle.Inline);
    });

    await using var session = store.LightweightSession();

    var personId = Guid.NewGuid();
    Console.WriteLine($"Creating person with ID: {personId}");

    // Start stream with PersonCreated event
    session.Events.StartStream<PersonAggregate>(personId, new PersonCreated(personId, "John", "Doe"));
    await session.SaveChangesAsync();
    Console.WriteLine("  -> Appended PersonCreated event");

    // Query the aggregate
    var person = await session.LoadAsync<PersonAggregate>(personId);
    Console.WriteLine($"  -> Loaded PersonAggregate: {person?.FirstName} {person?.LastName}");

    // Append another event
    session.Events.Append(personId, new PersonNameChanged(personId, "Jane", "Smith"));
    await session.SaveChangesAsync();
    Console.WriteLine("  -> Appended PersonNameChanged event");

    // Query again
    person = await session.LoadAsync<PersonAggregate>(personId);
    Console.WriteLine($"  -> Loaded PersonAggregate: {person?.FirstName} {person?.LastName}");

    // Check events
    var events = await session.Events.FetchStreamAsync(personId);
    Console.WriteLine($"  -> Stream has {events.Count} events");
}

// ============================================================================
// Test 2: EventProjection - flat projection that reacts to events
// ============================================================================
static async Task TestEventProjection(string connectionString)
{
    var store = DocumentStore.For(opts =>
    {
        opts.Connection(connectionString);
        opts.DatabaseSchemaName = "test2";
        opts.AutoCreateSchemaObjects = AutoCreate.All;

        // Register events
        opts.Events.AddEventType<PersonCreated>();
        opts.Events.AddEventType<PersonNameChanged>();

        // EventProjection: manually create and update documents
        opts.Projections.Add<PersonReadModelProjection>(ProjectionLifecycle.Inline);
    });

    await using var session = store.LightweightSession();

    var personId = Guid.NewGuid();
    Console.WriteLine($"Creating person with ID: {personId}");

    // Start stream - note: we can use any aggregate type marker, or just Guid
    session.Events.StartStream(personId, new PersonCreated(personId, "Alice", "Wonder"));
    await session.SaveChangesAsync();
    Console.WriteLine("  -> Appended PersonCreated event");

    // Query the read model
    var readModel = await session.LoadAsync<PersonReadModel>(personId);
    Console.WriteLine($"  -> Loaded PersonReadModel: {readModel?.FirstName} {readModel?.LastName}");

    // Append another event
    session.Events.Append(personId, new PersonNameChanged(personId, "Bob", "Builder"));
    await session.SaveChangesAsync();
    Console.WriteLine("  -> Appended PersonNameChanged event");

    // Query again
    readModel = await session.LoadAsync<PersonReadModel>(personId);
    Console.WriteLine($"  -> Loaded PersonReadModel: {readModel?.FirstName} {readModel?.LastName}");
}

// ============================================================================
// Test 3: SingleStreamProjection where projection document differs from stream type
// ============================================================================
static async Task TestSingleStreamProjectionWithCustomReadModel(string connectionString)
{
    var store = DocumentStore.For(opts =>
    {
        opts.Connection(connectionString);
        opts.DatabaseSchemaName = "test3";
        opts.AutoCreateSchemaObjects = AutoCreate.All;

        // Register events
        opts.Events.AddEventType<PersonCreated>();
        opts.Events.AddEventType<PersonNameChanged>();

        // Custom SingleStreamProjection
        opts.Projections.Add<PersonViewProjection>(ProjectionLifecycle.Inline);
    });

    await using var session = store.LightweightSession();

    var personId = Guid.NewGuid();
    Console.WriteLine($"Creating person with ID: {personId}");

    // Start stream with a marker aggregate type
    session.Events.StartStream<PersonAggregate>(personId, new PersonCreated(personId, "Charlie", "Chaplin"));
    await session.SaveChangesAsync();
    Console.WriteLine("  -> Appended PersonCreated event");

    // Query the view
    var view = await session.LoadAsync<PersonView>(personId);
    Console.WriteLine($"  -> Loaded PersonView: {view?.FullName}");

    // Append another event
    session.Events.Append(personId, new PersonNameChanged(personId, "David", "Bowie"));
    await session.SaveChangesAsync();
    Console.WriteLine("  -> Appended PersonNameChanged event");

    // Query again
    view = await session.LoadAsync<PersonView>(personId);
    Console.WriteLine($"  -> Loaded PersonView: {view?.FullName}");
}

// ============================================================================
// Events
// ============================================================================
public record PersonCreated(Guid PersonId, string FirstName, string LastName);
public record PersonNameChanged(Guid PersonId, string NewFirstName, string NewLastName);

// ============================================================================
// Test 1: Aggregate that IS the projection (standard pattern)
// ============================================================================
public class PersonAggregate
{
    public Guid Id { get; set; }
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";

    // Marten calls these methods automatically
    public static PersonAggregate Create(PersonCreated @event)
    {
        return new PersonAggregate
        {
            Id = @event.PersonId,
            FirstName = @event.FirstName,
            LastName = @event.LastName
        };
    }

    public void Apply(PersonNameChanged @event)
    {
        FirstName = @event.NewFirstName;
        LastName = @event.NewLastName;
    }
}

// ============================================================================
// Test 2: EventProjection with separate read model
// ============================================================================
public class PersonReadModel
{
    public Guid Id { get; set; }
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ModifiedAt { get; set; }
}

public class PersonReadModelProjection : EventProjection
{
    public PersonReadModel Create(IEvent<PersonCreated> @event)
    {
        Console.WriteLine($"    [Projection] Create called for PersonCreated, StreamId={@event.StreamId}");
        return new PersonReadModel
        {
            Id = @event.StreamId,
            FirstName = @event.Data.FirstName,
            LastName = @event.Data.LastName,
            CreatedAt = @event.Timestamp
        };
    }

    public void Project(IEvent<PersonNameChanged> @event, IDocumentOperations ops)
    {
        Console.WriteLine($"    [Projection] Project called for PersonNameChanged, PersonId={@event.Data.PersonId}");

        // For EventProjection, we need to manually load and update
        // Note: ops does NOT have a sync Load method, we need to use the tracking approach
        var model = ops.LoadAsync<PersonReadModel>(@event.Data.PersonId).GetAwaiter().GetResult();
        if (model is null)
        {
            Console.WriteLine($"    [Projection] WARNING: PersonReadModel not found for {@event.Data.PersonId}");
            return;
        }

        model.FirstName = @event.Data.NewFirstName;
        model.LastName = @event.Data.NewLastName;
        model.ModifiedAt = DateTimeOffset.UtcNow;
        ops.Store(model);
        Console.WriteLine($"    [Projection] Updated PersonReadModel to {model.FirstName} {model.LastName}");
    }
}

// ============================================================================
// Test 3: SingleStreamProjection with custom view document
// ============================================================================
public class PersonView
{
    public Guid Id { get; set; }
    public string FullName { get; set; } = "";
    public DateTimeOffset LastUpdated { get; set; }
}

public class PersonViewProjection : SingleStreamProjection<PersonView, Guid>
{
    public PersonView Create(IEvent<PersonCreated> @event)
    {
        Console.WriteLine($"    [SingleStreamProjection] Create called, StreamId={@event.StreamId}");
        return new PersonView
        {
            Id = @event.StreamId,
            FullName = $"{@event.Data.FirstName} {@event.Data.LastName}",
            LastUpdated = @event.Timestamp
        };
    }

    public void Apply(IEvent<PersonNameChanged> @event, PersonView view)
    {
        Console.WriteLine($"    [SingleStreamProjection] Apply called for PersonNameChanged");
        view.FullName = $"{@event.Data.NewFirstName} {@event.Data.NewLastName}";
        view.LastUpdated = @event.Timestamp;
    }
}
