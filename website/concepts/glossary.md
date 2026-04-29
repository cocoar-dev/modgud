# Glossar

Begriffe in cocoar.auth und ihre Entsprechung in anderen Identity-Systemen.

## Kernbegriffe

### Realm

Eine isolierte Identity-Boundary. Jeder Realm hat **seine eigene
PostgreSQL-Datenbank** (`cocoar_auth_next_<slug>`), eigene User, Rollen,
OAuth-Clients und Login-Provider.

Mapping zu anderen Systemen:

| cocoar.auth | Keycloak | Auth0 | Azure AD |
|---|---|---|---|
| Realm | Realm | Tenant | Tenant (Directory) |

Der **System-Realm** ist der erste Realm, der beim First-Time-Setup
automatisch entsteht. Er ist der einzige Realm der `CanManageTenants =
true` hat — d.h. nur seine User dürfen weitere Realms anlegen.

Die Realm-Boundary ist die **Domain** (Host-Header), nicht der URL-Pfad.
Realm `acme` lebt unter `acme.example.com`, System-Realm unter
`system.example.com` oder `localhost`.

### User

Ein Mensch oder Service-Account innerhalb eines Realms. User gehören zu
genau einem Realm. Identische Username in verschiedenen Realms sind
verschiedene Accounts.

Im Code: `Cocoar.Auth.Authentication.Domain.ApplicationUser` (ASP.NET
Core Identity-User).

### Group

Eine organisatorische Einheit. Gruppen haben Mitglieder (User oder
andere Gruppen) und tragen `PermissionRole`-Referenzen. Gruppen
existieren in zwei Modi:

- **Manual** — Admin pflegt Mitgliederliste
- **Auto** — Membership-Script bestimmt Mitglieder dynamisch

Siehe [Auto-Membership](/authorization-slice/auto-membership).

### PermissionRole

Eine benannte Bündelung von Permissions. Bindet eine Liste von Actions
an einen Resource-Type:

```
Name:         "User Manager"
ResourceType: "user"
Permissions:  ["read", "write"]
→ ergibt: user:read, user:write
```

### Permission

Ein String `<resource>:<action>` — z.B. `user:read`, `oauth-client:write`,
`app:admin`. Permissions fließen ausschließlich über Gruppen:

```
User → Group → Role → Permission
```

`<resource>:admin` ist Per-Resource-Bypass; `app:admin` ist globaler
Bypass. Siehe [Permissions & Gating](/authorization-slice/permissions).

### Session

Ein server-seitiger Eintrag (`UserSession`-Marten-Document) eines aktiven
Logins. Trackt IP, Browser, OS, Device-Type, `LastActiveAt`, `ExpiresAt`.
User können eigene Sessions revoken; Admins können User force-logout
machen. UAParser parst den User-Agent.

---

## OAuth / OIDC Begriffe

### Client (OAuth-Application)

Eine externe Application die User-Logins oder API-Zugriff anfordert.
Pro Realm angelegt — derselbe `client_id` in Realm A und Realm B sind
verschiedene Clients.

Pro Client konfigurierbar:

- **Client ID** — öffentlicher Identifier (z.B. `my-app`)
- **Client Secret** — privater Schlüssel (für Confidential Clients)
- **Redirect URIs** — erlaubte Callback-URLs
- **Grant Types** — welche Flows erlaubt sind
- **Access Token Type** — Reference (default) oder JWT

### Scope

Eine Permission-Boundary die ein Client requesten kann. Scopes
erscheinen im Token; Resource-Server entscheiden anhand der Scopes ob
der Request OK ist.

Default-Scopes (per Realm gesetzt beim Realm-Provisioning):

- `openid` — Required für OIDC, gibt User-ID zurück
- `profile` — Vorname, Nachname
- `email` — E-Mail-Adresse
- `roles` — Rollen-Mitgliedschaften
- `offline_access` — erlaubt Refresh-Tokens

### API (Resource)

Eine geschützte Backend-API. Hat einen Identifier (`audience`-Claim im
Token) und eine Liste der Scopes die sie unterstützt. Im Code:
`OAuthApiAggregate`.

### Grant Type

| Grant Type | Use Case |
|---|---|
| **Authorization Code + PKCE** | Web-Apps, SPAs, Mobile-Apps |
| **Client Credentials** | Machine-to-machine, Background-Services |
| **Refresh Token** | Renew expired access tokens |

::: warning Kein Implicit, kein ROPC
cocoar.auth unterstützt weder Implicit Flow noch Resource Owner
Password Credentials (ROPC). Beide gelten als unsicher und sind in
OAuth 2.1 deprecated.
:::

### Token-Typen

| Typ | Was es ist |
|---|---|
| **Access Token** | Zugriff auf APIs. Reference (opak, via Introspection) oder JWT (selbsttragend) |
| **Identity Token** | Wer hat sich eingeloggt — vom Client genutzt |
| **Refresh Token** | Neuen Access-Token holen ohne neuen Login |

### Access-Token-Format

Pro Client konfigurierbar:

| Format | So funktioniert's | Best für |
|---|---|---|
| **Reference** (default) | Opaker String. APIs validieren via Introspection-Endpoint. | SPAs, Mobile, Public Clients — instant Revocation. |
| **JWT** | Selbsttragender, signierter Token. APIs verifizieren lokal. | Trusted Backend-Services — kein Introspection-Roundtrip. |

::: tip Wann welchen?
**Reference Tokens** sind der sichere Default. Wenn Du einen Reference-Token
revokest, ist er sofort tot. JWTs können nicht revoked werden — sie sind
gültig bis sie expiren. JWT nur für Trusted Services wo der
Introspection-Roundtrip stört.
:::

---

## Login-Provider

Eine Authentifizierungs-Methode die User nutzen können. Pro Realm
konfigurierbar.

| Typ | Beschreibung |
|---|---|
| **Internal** | Built-in Username/Password. Immer da, nicht löschbar. |
| **OIDC** (`IdpConfig`) | Externe IdPs (Entra ID, Google, Auth0, ...). Authority + Client-ID + Secret + UserUpdateScript. |

Konfiguriert OIDC-Provider zeigen automatisch "Login with {Provider}"
Buttons im Login-UI.

---

## Begriffe im Code

| Begriff im Code | Begriff in der Doku/UI | Wo |
|---|---|---|
| `TenantId` | Realm Slug | Marten/Wolverine, Infrastructure-Layer |
| `Principal` | User oder Group oder Service-Account | Authorization-Slice (polymorph) |
| `Person` | User-Read-Model im Authorization-Slice | Eine Sub-Class von Principal |
| `Aggregate` | Event-sourced Entity | Domain-Layer |
| `*State` | Inline-Projection für sync. Konsistenz | Infrastructure-Layer |
| `*ListReadModel` / `*DetailsReadModel` | Async-Projection für Read-Optimization | Infrastructure-Layer |
| `IdpConfig` | OIDC-Provider-Configuration | Authentication-Slice |

::: info "Realm" vs. "Tenant"
User-facing heißt es überall **Realm**. Der Code nutzt **Tenant** im
Infrastructure-Layer (`TenantId`, `ITenantSessionFactory`,
`MasterTableTenancy`), weil das Marten und Wolverine so heißen. Selbe
Sache, zwei Namen.
:::
