# OAuth-Scopes

**Scopes** definieren, welche Berechtigungen ein OAuth-Client beim User anfragen darf — und welche Resources (APIs) das Token gegen ihn nutzen kann.

![OAuth-Scopes-Liste](/screenshots/admin-oauth-scopes.png)

## Standard-OIDC-Scopes (eingebaut)

cocoar.auth liefert die OIDC-Standard-Scopes immer mit:

| Scope | Was steckt drin? |
|-------|------------------|
| `openid` | Subject (User-ID) — Pflicht für jeden OIDC-Request |
| `profile` | Vor-/Nachname, Profilbild, Geburtstag |
| `email` | Email-Adresse + `email_verified`-Flag |
| `phone` | Telefonnummer + `phone_number_verified`-Flag |
| `address` | Adresse (falls hinterlegt) |
| `offline_access` | Erlaubt Refresh-Tokens |

Diese musst du nicht anlegen — sie sind immer verfügbar und werden im Consent-Screen passend übersetzt.

## Eigene Scopes definieren

Für deine eigenen APIs/Resources definierst du eigene Scopes — z.B. `timetodo.read`, `timetodo.write`, `crm.api`.

Administration → **OAuth → Scopes** → **„Erstellen"**.

### Felder

- **Name** — der technische Scope-String, exakt so wie er später in `scope=…` Requests erscheint (z.B. `timetodo.read`)
- **Anzeige-Name** — was im Consent-Screen erscheint („TimeToDo lesen")
- **Beschreibung** — Klartext-Erklärung im Consent-Screen („Erlaubt der App TimeToDo, deine Aufgaben zu lesen")
- **Resources** — Liste der Resource-URIs (Audience), für die Tokens mit diesem Scope ausgestellt werden

### Resources (Audience)

Eine **Resource-URI** identifiziert den Resource-Server (die API), der die Tokens akzeptiert. Beispiel:

- Scope: `timetodo.read`
- Resource: `https://api.timetodo.firma.at`

Wenn ein Client `scope=timetodo.read` anfragt und bekommt ein Access-Token, enthält dieses Token den Claim `aud=https://api.timetodo.firma.at` — die TimeToDo-API prüft genau diesen `aud`-Wert beim Token-Verify und akzeptiert sonst nicht.

::: warning Audience-Mismatch
Wird die Resource-URI hier anders geschrieben als die API später beim Validation prüft (z.B. `http` vs. `https`, trailing-slash, Port-Unterschied), schlägt jeder API-Request mit `401 Unauthorized — invalid audience` fehl. Halte beide Stellen synchron.
:::

## Scope einem Client erlauben

Im [OAuth-Client](./oauth-clients) → Tab **Scopes** → den neuen Scope in „Erlaubte Scopes" hinzufügen. Erst dann darf der Client den Scope in seinem Authorization-Request mitschicken.

## Scope löschen

Liste → Rechtsklick → **„Löschen"** (Soft-Delete).

::: warning Aktive Tokens bleiben gültig
Bereits ausgestellte Tokens, die diesen Scope enthalten, bleiben bis zu ihrer Lebensdauer gültig — das Löschen wirkt nur auf neu auszustellende Tokens. Bei kompromittierten Scopes solltest du zusätzlich alle aktiven Tokens revoken oder die kürzeste praktikable Token-Lifetime einstellen.
:::

## Tipps

::: tip Scope-Granularität
Eine Faustregel: ein Scope pro semantische Operation, nicht pro Endpunkt. Beispiel:

- gut: `timetodo.read`, `timetodo.write`, `timetodo.admin`
- schlecht: `timetodo.tasks.list`, `timetodo.tasks.detail`, `timetodo.tasks.create`, `timetodo.tasks.update`, …

Zu feingranular = Consent-Screen wird unleserlich. Zu grob = Apps brauchen mehr Power als nötig.
:::

::: tip Scopes mit Punkt-Namespacing
Eine Konvention: Scopes nach dem Schema `<resource>.<action>` benennen (`timetodo.read`, `crm.write`). Das macht es im Consent und in Token-Inspect-Tools sofort klar, welcher Scope zu welcher API gehört.
:::
