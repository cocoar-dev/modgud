# Sessions & Tokens

## Sessions (First-Party-Login)

Wenn ein User sich in cocoar.auth einloggt (Admin-UI, OAuth-Login-Page),
entsteht eine **Session** als `UserSession`-Marten-Document. Sessions
tracken:

- IP-Adresse
- Browser, Browser-Version
- Operating-System, OS-Version
- Device-Type (Desktop, Mobile, Tablet)
- `CreatedAt`, `LastActiveAt`, `ExpiresAt`

Der `User-Agent`-String wird mit **UAParser** gesplittet und gepflegt.

Sessions sind realm-skopiert (eine Session pro Realm pro Browser).
Login in Realm A betrifft Realm B nicht — ein User kann gleichzeitig in
mehreren Realms angemeldet sein, jeweils mit eigener Session.

### Session-Self-Service

Eingeloggte User sehen unter `/profile/sessions`:

- Alle aktiven Sessions
- Browser, OS, IP, "active now" oder "vor X Minuten"
- Pro Session: "Diese Session abmelden"
- Globaler Button: "Überall abmelden außer hier"

Endpoints:

```http
GET    /api/account/sessions
DELETE /api/account/sessions/{id}
DELETE /api/account/sessions
```

### Admin-Variante

```http
GET    /api/admin/users/{id}/sessions
DELETE /api/admin/users/{id}/sessions   # Force logout
```

Admin braucht `user:read` bzw. `user:write` (oder `:admin` Bypass).

## OAuth-Tokens

Wenn eine externe App via OAuth einen User authentifiziert, bekommt sie
Tokens. Drei Sorten:

### Access Token

Was die App an die API sendet um Zugriff zu beweisen. Pro Client als
einer von zwei Formaten konfiguriert:

| Format | Aussehen | API-Validierung |
|---|---|---|
| **Reference** (default) | Opaker String — nicht decodierbar | API ruft Introspection-Endpoint von cocoar.auth |
| **JWT** | Signierter JSON-Token — decodierbar | API verifiziert Signatur lokal |

- **Short-lived** — typisch 60 Min (per-Client konfigurierbar)
- **Reference-Tokens sind sofort revokierbar** — JWTs nur via Expiry

### Identity Token

Ein signierter JWT der dem Client sagt **wer eingeloggt ist**. Enthält
User-Info gemäß den granted Scopes (Name, E-Mail, Rollen). Wird vom
Client gelesen, nicht an APIs geschickt.

### Refresh Token

Ermöglicht es der App, neue Access-Tokens zu holen ohne den User wieder
anzumelden. Nur ausgegeben wenn `offline_access` granted ist.

- Long-lived (Tage bis Wochen, konfigurierbar)
- **Single-use mit Rotation** — jeder Use gibt einen neuen
  Refresh-Token zurück und invalidiert den alten
- Jederzeit revokierbar

## Token-Revocation

| Token-Typ | Wie revoken | Effekt |
|---|---|---|
| **Reference Access-Token** | `POST /connect/revoke` | Sofort ungültig |
| **JWT Access-Token** | `POST /connect/revoke` | Wirkt erst ab Expiry — JWT bleibt vorher valide |
| **Refresh-Token** | `POST /connect/revoke` | Sofort ungültig, keine neuen Access-Tokens möglich |
| **Session** (First-Party-Cookie) | Logout oder per Session-Management | Cookie ungültig, User muss sich neu anmelden |

## Token-Storage

Reference-Tokens und Refresh-Tokens werden als
`OpenIddictTokenDocument` in Marten gespeichert (per Tenant-DB). Direct
document storage — keine Event-Sourcing, weil Tokens kurzlebig und
ephemer sind.

Authorizations (Consent-Records, Permanent-Grants) sind
`OpenIddictAuthorizationDocument` — auch direct storage.

Tokens und Authorizations sind realm-isoliert per Tenant-DB.

## SignalR und Sessions

Der Vue-Admin-Frontend nutzt **SignalARRR** (typed bidirectional RPC
über SignalR) für Live-Updates. Die SignalR-Connection wird **nach**
dem Login aufgebaut, mit dem aktiven Auth-Cookie. Beim Logout macht
das Frontend einen `window.location`-Reload statt nur Vue-Router-Navigation
— sonst hängt eine alte Subscription am alten User.

Die SignalR-Group ist realm-skopiert (jeder Realm hat seinen eigenen
Hub-Channel). Cross-Realm-Notifications gibt es nicht.
