# Konzepte

## Principals & Capabilities

Ein `IPrincipal` ist alles, was per Id referenzierbar ist und
Berechtigungen tragen kann — User, Gruppen, Service-Accounts. Der
Minimalkontrakt:

```csharp
public interface IPrincipal
{
    Guid Id { get; }
    string Type { get; }       // Subclass-Override: "person" / "group" / "service-account"
    string DisplayName { get; }
    bool IsActive { get; }
    bool IsDeleted { get; }
}
```

`Type` ist im Slice ein abstract Getter auf `Principal`; jeder Subtyp
überschreibt mit einem stabilen Alias. Wird ins JSON serialisiert (nicht
JsonIgnore), damit Marten LINQ-Filter `p.Type == "person"` zu einem
JSONB-Path-Query übersetzen kann.

Spezifische Fähigkeiten (**Capabilities**) hängen an zusätzlichen
Interfaces:

```csharp
public interface IPrincipalWithMembers : IPrincipal
{
    IReadOnlyList<Guid> MemberIds { get; }
}

public interface IPrincipalWithAccount : IPrincipal
{
    string AccountName { get; }
}

public interface IPrincipalEmailAddressable : IPrincipal
{
    Task<IReadOnlyList<string>> GetEmailsAsync(IEmailResolutionContext ctx, CancellationToken ct);
}
```

Typische Kompositionen:

| Principal | Members | Account | Email | Bemerkung |
|---|---|---|---|---|
| `Person` | — | ✅ | ✅ | Konkret mit Firstname/Lastname/Acronym/Email/AccountName/ExternalIdentities |
| `Group` | ✅ | — | ✅ | Shared-Mailbox oder `ExpandToMembers` |
| `ServiceAccount` | — | ✅ | — | Nicht-menschlicher Principal, keine Notifications |

Die Slice-Services berühren nur das Interface, das sie brauchen:

- `PermissionService.GetUserGroupsAsync` traversiert via
  `IPrincipalWithMembers.MemberIds`
- `PrincipalEmailResolver.ResolveEmailsAsync` ruft
  `IPrincipalEmailAddressable.GetEmailsAsync`
- `PermissionEndpointFilter` interessiert sich für `IPrincipal.Id` via
  `ClaimTypes.NameIdentifier`

## Roles & Permissions

Eine Permission ist ein String `<resource>:<action>` — z.B.
`user:read`, `oauth-client:write`, `app:admin`. Eine `PermissionRole`
bindet eine Liste von Actions an einen Resource-Type:

```csharp
public class PermissionRole
{
    public string Name { get; set; }              // "User Manager"
    public string ResourceType { get; set; }      // "user"
    public List<string> Permissions { get; set; } // ["read", "write"]
    //  → user:read, user:write
}
```

Permissions fließen **nur** über Gruppen:

```
User → Group → Role → Permission
```

Keine direkten User → Role-Zuweisungen, keine User → Permission-Overrides.
Pfad: welche Gruppen ist der User in (transitiv, inkl. nested) → welche
Rollen haben diese Gruppen → welche Permissions resultieren.

### Bypass-Hierarchie

| String | Wirkung |
|---|---|
| `<resource>:admin` | Bypassed alle Action-Checks für diese Resource |
| `app:admin` | Bypassed alle Action-Checks für alle Resources (globaler Notausgang) |

`hasPermission(needed)` returns true wenn:

1. der User direkt diese Permission hat, oder
2. der User `<resource>:admin` für die zugehörige Resource hat, oder
3. der User `app:admin` hat

Der globale `app:admin`-Bypass ist absichtlich schmal — die
"System Admin"-Default-Rolle hat ihn, sonst niemand. Üblicherweise gibt
man pro Bereichs-Verantwortliche per-resource `<resource>:admin` (z.B.
"OAuth-Verantwortliche" bekommen `oauth-client:admin` +
`oauth-scope:admin` + `oauth-api:admin`, aber nicht `user:admin`).

## Access Scripts (ABAC)

Roles sagen **was** ein User darf (`user:read`). Access Scripts sagen
**welche Zeilen** er sehen darf. Das Script wird als
TypeScript-Arrow-Function gespeichert, zum Save-Zeitpunkt nach
JavaScript transpiliert, und am Query-Zeitpunkt in einen
LINQ-Expression-Tree übersetzt den Marten direkt in SQL umwandelt.

```typescript
// Skript auf einer Gruppe für ResourceType "user"
(u) => u.OrganizationalUnit === user.organizationalUnit
```

Wird zu (schematisch):

```sql
WHERE data->>'OrganizationalUnit' = '<user-ou>'
```

**Keine Roundtrips, kein In-Memory-Filtering.** Das ist der Punkt von
`Cocoar.JsEval.Linq`: JS-Function → C# `Expression<Func<TView, bool>>`
→ SQL. Wenn ein Script zu komplex ist (Loops, Closures, unübersetzbare
Built-ins), wirft der Translator — die Fehlermeldung landet beim Admin
der's gespeichert hat, nicht beim User.

Mehr Details siehe [Access Scripts](./access-scripts).

## Membership-Modi

Eine Gruppe ist entweder `Manual` oder `Auto`:

- **Manual** — Admin pflegt `MemberIds` direkt
- **Auto** — ein Membership-Script-Prädikat bestimmt die Mitglieder
  dynamisch. Bei jedem relevanten Principal-Event (Create, Update,
  Delete) wird neu berechnet

Das Membership-Script bekommt die gleiche generische Translation wie
Access-Scripts — eine einzelne SQL-Query gegen die Person-Table liefert
die neuen `MemberIds`. Für das Event-getriggerte
"hat sich der User geändert, betrifft das uns?"-Skip existiert ein
Dependency-Collector, der pro Script dokumentiert welche Properties er
liest (`"Firstname"`, `"Email"`). Änderungen außerhalb dieser
Property-Menge skippen die Neuberechnung.

Verschachtelte Gruppen sind erlaubt — eine Auto-Gruppe kann eine andere
(Manual oder Auto) als Mitglied haben, BFS-Traversal mit Visited-Set
gegen Zyklen.

Mehr Details siehe [Auto-Membership](./auto-membership).

## Events & Projections

Alle Mutations fließen als Events in den Marten-Event-Store:

| Event | Wann |
|---|---|
| `GroupCreatedEvent` | Create |
| `GroupUpdatedEvent` | Update |
| `GroupMembershipRecomputedEvent` | Auto-Membership neu berechnet, erfolgreich |
| `GroupMembershipRecomputeFailedEvent` | Script-Error — `MembershipLastError` gesetzt |
| `GroupDeletedEvent` | Delete |
| `PermissionRoleCreated/Updated/Deleted` | Role-CRUD |

Zwei Projektionen **inline** (synchron konsistent):

1. **`PrincipalProjectionBase`** — abstrakt, bearbeitet alle
   Group-Events. Die App erbt
   (`Cocoar.Auth.Authentication.Projections.AuthPrincipalProjection`)
   und ergänzt Apply-Methoden für ihre Person-Events
   (`UserCreatedEvent`, `UserUpdatedEvent` etc.). Die resultierenden
   Dokumente landen polymorph (via Marten `AddSubClass`) in der
   `mt_doc_principal`-Tabelle.

2. **`PermissionRoleProjection`** — Rollen landen in einer eigenen
   Tabelle.

Inline-Projektionen garantieren: *der nächste Query nach
`SaveChangesAsync()` sieht den Zustand*. Für Admin-UIs (Gruppe speichern
→ Dropdown aktualisieren) ist das Pflicht.

## Polymorphie

Alle Principals landen in einer Tabelle (`mt_doc_principal`). Marten
verwaltet den Discriminator (`mt_doc_type`) automatisch:

```csharp
martenOpts.Schema.For<Principal>()
    .AddSubClass<Person>("person")
    .AddSubClass<Group>("group")
    .AddSubClass<ServiceAccount>("service-account");
```

Alias `"person"` landet:

- in `mt_doc_type` (Marten Sub-Class-Discriminator, SQL-Spalte)
- im JSON unter `Type` (vom `Principal.Type`-Getter — Membership-Scripts
  nutzen `Type.Is(p, 'person')` für polymorph-sichere Typ-Checks)
- im STJ `$type` (für polymorphe Deserialisierung von `List<Principal>`)

Die Aliase sind unabhängig vom C#-Klassennamen — ein Class-Rename bricht
die Persistenz nicht.

## Query-Patterns

```csharp
// Alle Gruppen — Martens SubClass-Filter sorgt dafür, dass nur Group-Rows kommen
var groups = await session.Query<Group>()
    .Where(g => !g.IsDeleted)
    .ToListAsync();

// Gemischte Principals — die polymorphe Query liefert Person + Group + ServiceAccount
var all = await session.Query<Principal>()
    .Where(p => !p.IsDeleted)
    .ToListAsync();

// Typ-Filter in C#
var onlyGroups = all.OfType<Group>();
var onlyPersons = all.OfType<Person>();
```

`session.Query<TConcrete>()` ist SQL-level-gefiltert
(`WHERE mt_doc_type = 'group'`). `session.Query<Principal>()` scannt
die ganze Tabelle, polymorphe Deserialisierung. Für BFS im
`PermissionService` ist der polymorphe Scan OK weil alle Gruppen eh
einmalig geladen werden.

## Realm-Skopierung

Da der Slice auf der aktuellen Marten-Tenant-Session arbeitet, sind alle
Principals, Roles und Permissions **automatisch realm-isoliert**. In
Realm `acme` siehst Du nur Acme-Gruppen, in Realm `system` nur
System-Gruppen. Das ist die "Database-per-Tenant"-Konsequenz — der
Slice braucht dafür gar nichts zu wissen.
