# Auto-Membership

Eine Gruppe ist entweder `Manual` (Admin pflegt `MemberIds` direkt)
oder `Auto` (Membership-Script bestimmt Mitglieder dynamisch).

## Manual-Modus

```
Group "Backend Team"
  MembershipMode: Manual
  MemberIds: [<user-1>, <user-2>, <user-3>]
```

Admin fügt User per UI hinzu/raus. Nichts tut sich automatisch.

## Auto-Modus

```
Group "Sales Department"
  MembershipMode: Auto
  MembershipScript: (p) => p.OrganizationalUnit === "sales" && p.IsActive
  MemberIds: [<computed-from-script>]
```

`MemberIds` werden vom System gepflegt, nicht vom Admin. Bei jedem
relevanten Event (User created/updated/deleted) wird das Script neu
ausgewertet.

## Membership-Script

Genau wie ein Access-Script — TypeScript-Arrow-Function, auf einen
Person-Datensatz mappend auf `boolean`:

```typescript
// Predicate-Form
(p) => p.OrganizationalUnit === "engineering"
    && p.AccountName !== "service-account-bot"
    && p.IsActive

// Mit externalClaims (aus dem letzten OIDC-Login):
(p) => p.externalClaims.department === "Finance"
```

Wird mit Cocoar.JsEval.Linq zu einem
`Expression<Func<Person, bool>>` übersetzt → SQL gegen
`mt_doc_principal WHERE mt_doc_type = 'person'`. Eine einzelne Query
liefert die neuen `MemberIds`.

## Recompute-Trigger

`AutoMembershipSyncHandlers` (Wolverine-Handler) horchen auf
Person-Mutation-Events:

| Event | Aktion |
|---|---|
| `UserCreated` | Auto-Gruppen mit passendem Script-Predicate prüfen → bei Match: User adden |
| `UserUpdated` | Auto-Gruppen prüfen → User adden/removen je nach neuem Stand |
| `UserDeleted` | User aus allen Auto-Gruppen entfernen |
| `GroupMembershipScriptChanged` | Komplette Recompute-Pass für diese eine Gruppe |

## Dependency-Tracking (Selective Recompute)

Auto-Membership-Recompute ist teuer wenn man's bei jedem Heartbeat-Update
des Users macht. Lösung: pro Script wird beim Save ein
**Dependency-Set** der gelesenen Properties berechnet:

```typescript
// Script
(p) => p.OrganizationalUnit === "sales" && p.IsActive

// Dependencies
["OrganizationalUnit", "IsActive"]
```

Beim `UserUpdated`-Event wird gecheckt: hat sich eines der Felder aus
dem Dependency-Set geändert? Wenn nein → Recompute skippen für diese
Gruppe.

Beispiel: User updated `LastLoginAt`. `IsActive` und
`OrganizationalUnit` sind unverändert → Sales-Gruppe wird gar nicht
geprüft, obwohl `UserUpdated`-Event gefeuert hat.

## Failure-Handling

Wenn das Script wirft (Translator-Fehler oder Runtime-Fehler beim
Compile), wird ein `GroupMembershipRecomputeFailedEvent` mit dem
Fehler-Message gefeuert. Die Group-Projection setzt `MembershipLastError`
+ behält die alten `MemberIds`. Admin sieht den Fehler im
Group-Detail-View.

Erfolgreicher Recompute → `GroupMembershipRecomputedEvent` mit den
neuen `MemberIds`. `MembershipLastError` wird auf `null` gesetzt.

## Nested Auto-Gruppen

Eine Auto-Gruppe kann eine andere Gruppe (Manual oder Auto) als
Mitglied haben:

```
"All Staff" (Manual)
  Members: ["Engineering", "Sales", "Support"]   ← drei Auto-Gruppen

"Engineering" (Auto)
  Script: (p) => p.OrganizationalUnit === "engineering"

"Sales" (Auto)
  Script: (p) => p.OrganizationalUnit === "sales"
```

Permission-BFS expandiert das ohne Sonderfall — `IPrincipalWithMembers`
ist polymorph. Cycle-Detection via Visited-Set.

## Initial-Recompute

Wenn ein Admin eine Auto-Gruppe neu erstellt (oder das Script ändert),
läuft ein initialer Voll-Pass:

```csharp
// IAutoMembershipRecalculator
await recalculator.RecomputeAllMembersAsync(group, ct);
```

→ ein einzelner SQL-Query gegen alle Person-Dokumente, das Script als
WHERE-Klausel. Resultat → `MemberIds` setzen + Event fire.

Bei einer Million Persons könnte das langsam sein — aktuell ist
cocoar.auth aber für eine Größenordnung weit drunter ausgelegt
(Mid-Sized SaaS Org-Charts, ein paar Tausend User pro Realm).

## Beispiel-Setup

```
Group "OU Sales" (Auto)
  Script: (p) => p.OrganizationalUnit === "sales" && p.IsActive
  Roles: ["Sales Read", "Customer Manager"]

Group "Active Engineers" (Auto)
  Script: (p) => p.Department === "engineering"
              && p.IsActive
              && !p.AccountName.startsWith("svc-")
  Roles: ["Code Repo Reader", "CI Trigger"]
```

Wenn ein neuer Sales-User per OIDC-Login eingerichtet wird:

1. `UserCreated` mit `OrganizationalUnit=sales` feuert
2. `AutoMembershipSyncHandlers` evaluiert beide Auto-Scripts:
   - "OU Sales" matched → User wird zur Membership added
   - "Active Engineers" matched nicht → kein Effekt
3. `GroupMembershipRecomputedEvent` fired für "OU Sales"
4. SignalR-Notification an alle Admin-Browser → die Gruppen-Liste im
   Frontend updated automatisch (über `useEntityService`-Subscriptions)
5. User erbt automatisch alle Permissions die "OU Sales" hat → kann
   sofort Customer-Daten sehen, ohne dass jemand was klickt
