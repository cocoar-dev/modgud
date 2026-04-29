# Access Scripts (ABAC)

Roles entscheiden **was** ein User darf (`user:write`), Access Scripts
entscheiden **welche Zeilen** er sehen oder bearbeiten darf. Beide
Achsen sind unabhängig — eine Permission ohne Access-Script bedeutet
"alle Zeilen", ein Access-Script ohne Permission bedeutet gar nichts.

## Was sind Access Scripts?

Pro Gruppe × Resource definiert der Admin ein **TypeScript-Arrow-Function**
das einen einzelnen Datensatz auf `boolean` mapped:

```typescript
// Auf der Gruppe "External Auditors" für Resource "user":
(u) => u.IsActive && u.OrganizationalUnit === user.organizationalUnit
```

Das Script wird beim Speichern:

1. Nach JavaScript transpiliert (Cocoar.JsEval.TypeScript)
2. Validiert (Cocoar.JsEval.Linq prüft ob es zu LINQ übersetzbar ist)
3. Im Marten-Document der Gruppe gespeichert (Source + transpiliertes JS)

Beim Query (z.B. `GET /api/admin/users`):

1. Backend lädt alle Gruppen des aktuellen Users
2. Für jede Gruppe + Resource "user" wird das Script geladen
3. Cocoar.JsEval.Linq transpiliert JS → C# `Expression<Func<UserDoc, bool>>`
4. Marten setzt das in WHERE-Klauseln um → eine SQL-Query
5. Ergebnis: nur die Zeilen die mindestens ein Script erlaubt

## Warum nicht in C#?

Weil die Scripts vom **Admin** im Frontend gepflegt werden — ohne
Code-Deploy, ohne Build. Monaco-Editor (über `@cocoar/vue-script-editor`)
gibt Syntax-Highlighting + Type-Checks gegen die Resource-Schema.

## Schema-Beispiel

```typescript
// Implicit verfügbare Variablen im Script:
// - <param>: der Datensatz vom Resource-Type
// - user: der eingeloggte User mit:
//     id, accountName, email, organizationalUnit,
//     externalClaims (alle Claims aus der OIDC-Session)

(u) => u.OrganizationalUnit === user.organizationalUnit
    && u.IsActive
    && !u.IsDeleted

// OAuth-Client filtern auf "nur eigene":
(c) => c.OwnerUserId === user.id

// Komplexer mit Array.some:
(role) => role.Tags.some(t => t === user.externalClaims.department)
```

## Translatable Operations

Cocoar.JsEval.Linq kennt einen abgegrenzten Subset von JavaScript:

| OK | Nicht OK |
|---|---|
| Vergleichsoperatoren `=== !== < > <= >=` | `==` `!=` (Loose) |
| Boolean-Operatoren `&& \|\| !` | bitwise `& \| ^` |
| String-Operationen `===`, `.startsWith`, `.endsWith`, `.includes` | RegExp `.match`, `.test` |
| Array `.some`, `.every`, `.includes`, `.length` | Array `.map`, `.filter`, `.reduce` (no closures) |
| Closures auf `user.*` und Konstanten | Closures auf willkürliche Funktionen |
| Property-Access `obj.field` | Computed-Property `obj[expr]` |
| Optional-Chaining `obj?.field` | Numeric-Casts `+x` `~~x` |

Wenn das Script unübersetzbar ist, wirft der Translator beim Save mit
einer klaren Fehlermeldung — Admin sieht im Editor sofort den Fehler.

## Resource-Schema

Beim Resource-Register kann jede Resource ihr Schema mitliefern (für
Editor-Type-Checks):

```csharp
opts.RegisterResource("user", schema => schema
    .Property("Id", typeof(Guid))
    .Property("UserName", typeof(string))
    .Property("Email", typeof(string))
    .Property("FirstName", typeof(string))
    .Property("LastName", typeof(string))
    .Property("OrganizationalUnit", typeof(string))
    .Property("IsActive", typeof(bool))
    .Property("IsDeleted", typeof(bool)));
```

Das Schema fließt ins Frontend, Monaco gibt Auto-Complete und
Type-Errors. Backend nutzt das gleiche Schema zur Translation-Validation.

## Mehrere Scripts kombinieren

Ein User ist meist in mehreren Gruppen — jede kann ein Script auf
dieselbe Resource haben. Die Scripts werden mit **OR** verbunden:

```sql
WHERE (data->>'OrganizationalUnit' = 'sales')   -- Gruppe "Sales"
   OR (data->>'OrganizationalUnit' = 'support') -- Gruppe "Support"
   OR (data->>'OwnerUserId' = '<user-id>')      -- Gruppe "Owners"
```

Wenn keine der Gruppen ein Script auf die Resource hat → "alle Zeilen
sichtbar". Das matched die Permission-Achse: ohne `user:read` siehst Du
gar nichts; mit `user:read` siehst Du alles was die Scripts erlauben
(oder alles, wenn keine Scripts).

## Implicit "Sees own" für Person

`Person` (= eingeloggter User) ist immer in seinem eigenen Resultat
sichtbar — ohne explizites Script. Sonst könnte ein User sich selber
nicht finden, was Profil-Anzeigen kaputt macht.

## ABAC-Demo-Seed (optional)

Beim First-Time-Setup kann der Admin per Checkbox einen ABAC-Demo-Seed
mit einrichten:

- Drei Demo-User mit verschiedenen `OrganizationalUnit`-Werten
- Eine "OU Auditor"-Gruppe mit Script
  `(u) => u.OrganizationalUnit === user.organizationalUnit`
- Eine "Self-Service"-Gruppe mit Script
  `(u) => u.Id === user.id`

So sieht der Admin sofort wie das Zusammenspiel von Roles + Scripts in
einem konkreten Setup aussieht.

## Policy-Simulator

Im Admin-UI gibt es einen `/admin/simulator`-View
(`AuthorizationSimulatorView.vue`):

- Wähle einen User
- Wähle eine Resource
- Sieh die effektiven Permissions, die aktiven Scripts und das
  resultierende SQL-Fragment

Hilft beim Debuggen "warum sieht User X den Datensatz Y nicht".

## Performance-Modell

- Pro Request: 1 Query "welche Gruppen ist User in" + 1 Query "Scripts
  für Resource Y in diesen Gruppen" + 1 Query auf die eigentliche
  Resource (mit den Scripts als WHERE)
- Marten cached Inline-Projection-Reads im Session-Scope
- Bei vielen Gruppen + vielen Scripts kann die WHERE-Klausel groß
  werden — PostgreSQL kommt damit aber gut klar (planner ist OK mit
  großen OR-Ketten)
- Wenn ein Script unübersetzbar ist (sollte eigentlich beim Save
  gefangen werden), fällt der Endpoint hart durch — keine
  Silent-Degradation auf In-Memory-Filter
