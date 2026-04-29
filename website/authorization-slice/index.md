# Cocoar.Auth.Authorization

Vertical Slice für das Authorization-Kernsystem von cocoar.auth. Eigenes
C#-Projekt (`src/dotnet/Cocoar.Auth.Authorization/`), als Kopie von
TimeToDos gleichnamigem Slice in cocoar.auth eingezogen und um
IdP-spezifische Resources erweitert.

## Was im Slice ist

- **Principals mit Capability-Interfaces** — `Person`, `Group`,
  `ServiceAccount`, alles was per Id referenzierbar ist und Berechtigungen
  tragen kann
- **Roles + Permissions** — RBAC mit freier Resource/Action-Registrierung
  via `IResourceRegistry`. cocoar.auth registriert: `user`, `permission-role`,
  `authorization-group`, `oauth-client`, `oauth-scope`, `oauth-api`,
  `login-provider`, `idp-config`, `realm`, `auth-log`, `app`
- **Granular Per-Resource-Gating** — `<resource>:<action>` Strings
  (`user:read`, `oauth-client:write`); `<resource>:admin` ist
  Per-Resource-Bypass; `app:admin` ist globaler Bypass
- **Access Scripts** — TypeScript-Arrow-Functions pro Gruppe × Resource,
  werden nach JavaScript transpiliert und am Request zu
  Marten-LINQ-Queries kompiliert (`Cocoar.JsEval.Linq`)
- **Auto-Membership** — Gruppen deren Mitglieder von einem
  Predicate-Script bestimmt werden, inkl. Dependency-Tracking für
  selektive Recalculation
- **ASP.NET-Core-Extension** —
  `.RequiresPermission("oauth-client:write")` als Endpoint-Filter
- **Marten-Integration** — polymorphe Storage via Sub-Class-Mapping
  (Person + Group + ServiceAccount im selben `mt_doc_principal`-Table),
  Inline-Projection für synchrone Konsistenz, Wolverine-Commands für
  CRUD

## Was der Slice bewusst nicht macht

- **Keine Authentication** — Login, 2FA, Passkey, OIDC liegen im
  Authentication-Slice
- **Keine HTTP-Endpoints für CRUD** — die App definiert ihre Endpoints
  und dispatcht via `IMessageBus.InvokeAsync<ErrorOr<Group>>(command)`
- **Keine Storage-Abstraktion** — Marten + Wolverine + Event-Sourcing
  sind gesetzt
- **Kein Mandanten-Routing** — der Slice arbeitet stets gegen die
  aktuelle Marten-Tenant-Session. Realm-Routing macht
  `RealmMiddleware` im Api-Layer

## Grenzlinie zum Authentication-Slice

| Verantwortung | Authorization-Slice | Authentication-Slice |
|---|---|---|
| Wer ist dieser User? | — | ✅ |
| Wie heißt der User? | Read-Model: `Person` (Firstname etc.) | ✅ Identity-Adapter füllt es |
| Welche Gruppen ist er in? | ✅ | — |
| Welche Rollen hat er? | ✅ | — |
| Darf er das? | ✅ | — |
| Darf er diese Zeile sehen? | ✅ via Access-Scripts | — |

`Person` ist die Brücke: identitäts-shaped Felder werden vom
Authentication-Stack befüllt (über die App-spezifische
`PrincipalProjection`, die `PrincipalProjectionBase` erbt), der
Authorization-Slice nutzt sie als Read-Model für Email-Routing +
Membership-Predicates.

## ResourceRegistry

Der zentrale Knoten für alle Permission-Strings. Der Authorization-Slice
liefert das Interface; jede App registriert ihre Resources beim Boot:

```csharp
services.AddCocoarAuthAuthorization(opts =>
{
    opts.RegisterResource("user");
    opts.RegisterResource("permission-role");
    opts.RegisterResource("authorization-group");
    opts.RegisterResource("oauth-client");
    opts.RegisterResource("oauth-scope");
    opts.RegisterResource("oauth-api");
    opts.RegisterResource("login-provider");
    opts.RegisterResource("idp-config");
    opts.RegisterResource("realm");
    opts.RegisterResource("auth-log");
    opts.RegisterResource("app");  // global bypass
});
```

Pro Resource sind die Standard-Actions `read`, `write`, `delete`, `admin`
verfügbar. Das Admin-UI zeigt im Role-Editor diese Liste, das Backend
prüft die Strings beim `RequiresPermission`.

## Default-Roles im Setup

Beim First-Time-Setup erstellt cocoar.auth drei Default-Roles und legt
den ersten Admin in der "System-Admin"-Gruppe ab:

| Rolle | Permissions |
|---|---|
| **System Admin** | `app:admin` (globaler Bypass) |
| **User Manager** | `user:read`, `user:write`, `permission-role:read`, `authorization-group:read`, `authorization-group:write` |
| **Viewer** | `user:read`, `permission-role:read`, `authorization-group:read`, `oauth-client:read`, `oauth-scope:read` |

## Abhängigkeiten

| Hart | Begründung |
|---|---|
| Marten 8+ | Event-Store + Polymorphic Document Storage (Sub-Class-Mapping) |
| WolverineFx.Marten | Commands + Handler-Discovery + Outbox |
| Cocoar.JsEval + .Linq + .TypeScript | TS → JS Transpile + JS → Expression-Tree-Translation |
| ErrorOr | Command-Return-Typen |
| Microsoft.AspNetCore.App | `IEndpointFilter` für `RequiresPermission` |

## Status

Cocoar.Auth nutzt diesen Slice produktiv. Wired über
`UseCocoarAuthAuthorization()` in der Marten-Konfiguration und
`services.AddCocoarAuthAuthorization()` in der DI.

## Inhaltsverzeichnis

- [Konzepte](./konzepte) — Mental-Model, Polymorphie, Events, Projections
- [Permissions & Gating](./permissions) — Per-Resource-Gating, Sidebar, Endpoint-Filter
- [Access Scripts (ABAC)](./access-scripts) — TypeScript-Arrow-Functions pro Resource
- [Auto-Membership](./auto-membership) — Gruppen mit Predicate-Membership
