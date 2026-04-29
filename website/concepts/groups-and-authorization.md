# Autorisierung & ABAC

cocoar.auth nutzt eine kombinierte **RBAC + ABAC**-Architektur:

- **RBAC** (Role-Based Access Control) entscheidet **was** ein User
  darf (`user:write`, `oauth-client:read`)
- **ABAC** (Attribute-Based Access Control) entscheidet **welche
  Zeilen** er sehen oder bearbeiten darf — über JavaScript-basierte
  Access-Scripts pro Gruppe × Resource

Beide Achsen sind unabhängig und implementiert im
**Authorization-Slice** (`Cocoar.Auth.Authorization`).

## RBAC: User → Group → Role → Permission

Permissions fließen **ausschließlich** über Gruppen:

```
User ──► Group ──► PermissionRole ──► "<resource>:<action>"
```

Keine direkten User → Role-Zuweisungen, keine User → Permission-Overrides.
Pfad: welche Gruppen ist der User in (transitiv, inkl. nested) → welche
Rollen haben diese Gruppen → welche Permissions resultieren.

### Permission-Format

`<resource>:<action>` — z.B.:

| Permission | Bedeutung |
|---|---|
| `user:read` | User lesen |
| `user:write` | User erstellen/ändern |
| `user:delete` | User löschen |
| `oauth-client:write` | OAuth-Clients pflegen |
| `realm:read` | Realm-Liste sehen (nur in Tenant-Manager-Realms) |
| `<resource>:admin` | **Per-Resource-Bypass** für alle Actions dieser Resource |
| `app:admin` | **Globaler Bypass** für alle Resources |

Alle Resource-Strings in cocoar.auth siehe
[Permissions & Gating](/authorization-slice/permissions).

### Bypass-Hierarchie

`hasPermission(needed)` returns true wenn:

1. der User direkt diese Permission hat, oder
2. der User `<resource>:admin` für die zugehörige Resource hat, oder
3. der User `app:admin` hat

Der globale `app:admin`-Bypass ist absichtlich schmal — die
"System Admin"-Default-Rolle hat ihn, sonst niemand.

### Default-Rollen pro Realm

| Rolle | Permissions |
|---|---|
| **System Admin** | `app:admin` |
| **User Manager** | `user:read/write`, `permission-role:read`, `authorization-group:read/write` |
| **Viewer** | Read-only auf User, Roles, Groups, OAuth-Clients, OAuth-Scopes |

Beim First-Time-Setup wird der erste User der "System Admin"-Gruppe
zugewiesen → bekommt `app:admin` → sieht alles.

## Gruppen

`Group` ist der Träger von Permissions. Eine Gruppe hat:

- `Name`, `Description`
- `MembershipMode`: `Manual` oder `Auto`
- `MemberIds`: User oder andere Gruppen (nested)
- Referenzen auf `PermissionRole`s
- Optional: Membership-Script (bei Auto-Modus)
- Optional: Access-Scripts pro Resource (für ABAC)

### Manual vs Auto

- **Manual**: Admin pflegt `MemberIds` direkt
- **Auto**: Membership-Script-Predicate bestimmt die Mitglieder
  dynamisch. Wird bei jedem User-Mutation-Event neu evaluiert.

Siehe [Auto-Membership](/authorization-slice/auto-membership).

### Nested Groups

Eine Gruppe kann andere Gruppen als Member haben:

```
"All Staff" (Manual)
  ├── "Engineering" (Auto: OU=engineering)
  ├── "Sales" (Auto: OU=sales)
  └── "Support" (Auto: OU=support)
```

Permission-BFS expandiert das ohne Sonderfall — `IPrincipalWithMembers`
ist polymorph. Cycle-Detection via Visited-Set.

## ABAC: Access Scripts

Roles sagen **was** ein User darf (`user:read`). Access Scripts sagen
**welche Zeilen** er lesen darf. Pro Gruppe × Resource definiert der
Admin ein TypeScript-Arrow-Function:

```typescript
// Auf der Gruppe "External Auditors" für Resource "user":
(u) => u.OrganizationalUnit === user.organizationalUnit && u.IsActive
```

Wird zu (schematisch):

```sql
WHERE data->>'OrganizationalUnit' = '<user-ou>'
  AND data->>'IsActive' = 'true'
```

**Keine Roundtrips, kein In-Memory-Filtering.** Cocoar.JsEval.Linq
übersetzt JS → C# `Expression<Func<TView, bool>>` → SQL.

### Script-OR-Kombination

Ein User ist meist in mehreren Gruppen — jede kann ein Script auf
dieselbe Resource haben. Die Scripts werden mit **OR** verbunden:

```sql
WHERE (data->>'OrganizationalUnit' = 'sales')   -- Gruppe "Sales"
   OR (data->>'OrganizationalUnit' = 'support') -- Gruppe "Support"
   OR (data->>'OwnerUserId' = '<user-id>')      -- Gruppe "Owners"
```

Wenn keine der Gruppen ein Script auf die Resource hat → "alle Zeilen
sichtbar" (= klassisches RBAC).

Mehr siehe [Access Scripts](/authorization-slice/access-scripts).

## Auflösung im Detail

```
Request mit Cookie kommt rein
  ↓
PermissionEndpointFilter
  ↓
ClaimTypes.NameIdentifier → UserId
  ↓
IPermissionService.GetEffectivePermissionsAsync(userId)
  ├── BFS durch alle Group-Membership (transitiv, mit Visited-Set)
  ├── für jede Gruppe: load PermissionRole-Refs
  ├── für jede Rolle: expand Permissions
  └── Set<string> aller "<resource>:<action>"
  ↓
Bypass-Checks:
  hat "app:admin"? → ✓
  hat "<resource>:admin"? → ✓
  hat exakt needed? → ✓
  sonst → 403
```

## Sidebar-Mirror

Das Frontend spiegelt diese Logik 1:1: in `views/admin/AdminView.vue`
deklarieren Sidebar-Items welche Permissions sie sichtbar machen
sollen. Backend und Frontend verwenden exakt dieselben Strings —
"Single Source of Truth" sind die Permission-Konstanten.

```typescript
{ section: 'authorization', label: 'nav.users', icon: 'users',
  path: '/admin/users', requirePermissions: ['user:read'] }
```

Ein User mit nur `user:read` sieht nur "Users" in der Sidebar — keine
OAuth, keine System.

## Was diese Architektur NICHT ist

- **Kein Deny-Grant** — nur positive Grants. Effective Access ist immer
  Union. Simplicity over Flexibility.
- **Keine implicit Permissions** — Group-Membership grants per se gar
  nichts; Roles müssen explizit assigned werden.
- **Keine User → Role direkt** — alles läuft über Gruppen.
- **Keine zeitbasierten Conditions** im Script-Kern (z.B.
  "if time > 18:00") — Scripts sind Set-Filter, keine
  Decision-Engines.
