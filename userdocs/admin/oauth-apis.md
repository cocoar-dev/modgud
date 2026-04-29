# OAuth-APIs (Resource-Server)

Eine **OAuth-API** in cocoar.auth ist die Registrierung eines **Resource-Servers** — also einer API, die Access-Tokens von cocoar.auth validieren und akzeptieren will.

::: info Was unterscheidet API von Client?
- **OAuth-Client** = die App, die User-Login durchführt und Tokens **bekommt**
- **OAuth-API** = die API, die Tokens **validiert** und Requests damit autorisiert

Eine App kann beides sein (z.B. ein BFF-Pattern: User-Login als Client, eigene API als API).
:::

![OAuth-APIs-Liste](/screenshots/admin-oauth-apis.png)

## Wann brauche ich eine OAuth-API-Registrierung?

In den meisten Fällen reicht es, im [Scope](./oauth-scopes) eine Resource-URI einzutragen — die API kann dann mit Standard-OIDC-Discovery alles prüfen. Eine **explizite OAuth-API-Registrierung** brauchst du wenn:

- Die Resource-API will sich am OAuth-Server selbst **authentifizieren** (z.B. für Token-Introspection)
- Du willst **Multi-Secrets** (mehrere parallel gültige Secrets, z.B. für nahtlose Rotation)
- Die API braucht **eigene Scopes-Listen** für Discovery

## API anlegen

Administration → **OAuth → APIs** → **„Erstellen"**.

### Pflichtfelder

- **Name** — technischer Bezeichner (z.B. `timetodo-api`)
- **Anzeige-Name** — fürs Admin-UI
- **Audience** — die Resource-URI, exakt wie sie in den Token-Claims erwartet wird (z.B. `https://api.timetodo.firma.at`)

### Optionale Felder

- **Beschreibung**
- **Erlaubte Scopes** — welche Scopes „gehören" zu dieser API (für Discovery / Doku-Zwecke)

## Multi-Secrets

OAuth-APIs unterstützen **mehrere parallel gültige Secrets** — perfekt für Zero-Downtime-Rotation:

![Multi-Secrets-Tab](/screenshots/admin-oauth-api-secrets.png)

### Secret hinzufügen

Detail → Tab **Secrets** → **„Neues Secret"**.

- **Label** (zur Unterscheidung — z.B. `prod-2026-01`, `prod-2026-04`)
- **Ablaufdatum** (optional — danach automatisch ungültig)

Beim Erstellen wird der Klartext **einmalig** angezeigt — sofort in den Vault.

### Rollover-Strategie

1. Neues Secret anlegen (`new`) — alte (`old`) bleibt aktiv
2. API-Konfiguration aktualisieren — alle Instanzen nutzen jetzt `new`
3. 24h beobachten ob Token-Validation noch funktioniert
4. `old` ablaufen lassen oder explizit löschen

So gibt es **keinen** Moment, in dem die API kein gültiges Secret hat.

::: warning Mindestens ein aktives Secret behalten
Löschst du das letzte aktive Secret, kann die API sich nicht mehr beim cocoar.auth authentifizieren — Token-Introspection und administrative Calls schlagen fehl.
:::

### Secret löschen

Liste → Mülleimer → bestätigen.

## Token-Validation in deiner API

In deiner API (z.B. ASP.NET Core) konfigurierst du JWT-Bearer-Auth so:

```csharp
.AddJwtBearer(options =>
{
    options.Authority = "https://auth.firma.at"; // cocoar.auth-Discovery
    options.Audience  = "https://api.timetodo.firma.at"; // exakt = Audience aus dem Admin
    options.TokenValidationParameters.ValidateIssuer  = true;
    options.TokenValidationParameters.ValidateAudience = true;
})
```

cocoar.auth liefert das JWKS automatisch unter `/.well-known/jwks` — die API holt sich die Public-Keys und validiert die Token-Signatur.

## API deaktivieren / löschen

Detail → **„Deaktivieren"** stoppt sofort alle Token-Validierungen für diese Audience.

Liste → Rechtsklick → **Löschen** (Soft-Delete).

## Häufige Probleme

| Fehler | Ursache / Fix |
|--------|---------------|
| `401 invalid audience` | API-`Audience`-Config != Audience im Admin. Beide auf den exakt gleichen String setzen (Schema, Port, Trailing-Slash). |
| `401 invalid signature` | API kann JWKS nicht laden (Firewall? Falsche Authority-URL?) → JWKS-URL manuell mit `curl` testen. |
| `401 token expired` | Token-Lifetime kurz, Clock-Skew zwischen API und cocoar.auth → Server-Zeit synchronisieren (NTP). |
| Multi-Secret-Confusion | Sicherstellen dass die API tatsächlich gegen das aktuell aktive Secret prüft, nicht gegen das gelöschte. |
