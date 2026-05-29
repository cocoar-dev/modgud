# Federation v1 — Implementierungs-Spec

Status: **Design beschlossen (A–G entschieden, 2026-05-29), bereit für einen Implementierungsplan. Noch kein Code.** Konkretisiert das in [identity-lifecycle-untangle](./identity-lifecycle-untangle#federation-prior-art) beschlossene Federation-Modell zu echten Code-Seams. Grundlage: Integrations-Map `wf_63933d9f-149`. Setzt Hotfix C (PR #21, `766c9f8`) voraus.

> **Hintergrund** (Prior-Art, Stale-Admin-Falle, Hub-vs-Broker-Spektrum): siehe das [Untangle-Doc](./identity-lifecycle-untangle). Dieses Doc ist die Bau-Vorlage.

## Das Modell in einem Absatz

Hub-vs-Federation ist kein Realm-*Modus*, sondern eine Eigenschaft der Herleitung *jeder einzelnen Mitgliedschaft*. Externe Claims werden bei jedem Login gelesen, transformiert und mit `source`-Tag + `capturedAt`-Timestamp auf einem **Claims-per-Source-Store** an der Person persistiert (der aktuelle Provider wird dabei delete+rewrite refresht). Gruppen-Membership wird aus diesen Claims berechnet — durch eine Engine, gesteuert durch einen **Zwei-Ebenen-Source-Filter**. Identität bleibt strikt Hub (Token/UserInfo tragen nur Modgud-Rollen/Permissions, nie rohe Upstream-Claims oder Gruppen). **Die Session ist der Lease — wörtlich:** extern-abgeleitete Authz gilt, solange die Session/das Grant gültig ist, und endet, wenn die Session endet. Kein Mid-Session-Verfall-Timer (siehe [E](#e-session--lease)).

## Zwei Architektur-Befunde, die alles prägen

1. **Ein einziger Seam.** OIDC *und* SAML konvergieren auf **`ExternalLoginProcessor.ProcessAsync`** (`ExternalLoginProcessor.cs:40-237`) — OIDC aus `ExternalAuthEndpoints.cs:144`, SAML aus `SamlLoginFlow.cs:202`. Federation wird **einmal** geschrieben; SAML braucht keine eigene Änderung.
2. **Autorisierung wird SPÄT aufgelöst.** Cookie/Session tragen heute null Authz (`Success()`, `ExternalLoginProcessor.cs:334-365`). Rollen/Permissions entstehen erst zur Auflösungs-Zeit: `BuildResourceAccessAsync:556-597` → `IPermissionService` → BFS über persistierte `Group.MemberIds`. Human-Flow liefert das via **UserInfo** (Token ist lean), M2M via Token-Claim. Folge: session-abgeleitete Memberships müssen dort **unioned** werden.

## Datenmodell

### Claims-per-Source-Store (neu)
Per-User persistiertes Marten-Dokument (keyed auf `userId`, nicht event-sourced — refresh-bare Snapshot-Daten), flache Liste:
```
ClaimEntry { source, type, value, capturedAt }
  source     = "local" | "provider:<slug>"
  type       = Claim-Name ("groups", "department", "email", …)
  value      = string | string[]
  capturedAt = Zeitpunkt dieses Logins
```
- **Lokale Identität** lebt auf der `Person` (getypte Felder) = `source="local"`; sie wird zur Eval-Zeit in die Claims-Sicht projiziert, nicht dupliziert.
- **Externe Claims** (`source="provider:<slug>"`): bei jedem Login wird `provider:<X>` (X = benutzter Provider) **komplett gelöscht + neu geschrieben** (SET/FORCE-Reconcile). Lokale + andere Provider unberührt.
- `capturedAt`: für Was-wäre-wenn-Alter + v2-Lease + Staleness-Anzeige. **In v1 NICHT als Drop-Timer durchgesetzt** (siehe E).
- **PII-Pflicht:** GDPR-Erase + Delete-Pfade müssen den Store mit-scrubben (Masking-Rule + Delete, Muster aus Hotfix C).

### Transform-Ausgabe: standardisiertes `ResolvedClaims` (nur claims)
Jeder Provider-Login produziert über die Transform-Stufe (`UserUpdateScriptRunner`/Jint) ein standardisiertes Objekt mit **nur Claims** — kein privilegiertes `groups`. „Groups" ist Provider-Vokabular (EntraID `groups`, SAML `memberOf`, …) und landet einfach als `claims.groups`-Eintrag. Das Script normalisiert/berechnet Claims; nachgelagert lesen alle uniform `claims`.

### Profil-Patch: getrennt, mit Authoritative-Gate (neu)
4 Profilfelder bleiben first-class auf dem Modgud-User, getrennt gepatcht (dünner Schritt liest well-known Claims → User-Felder). **Neu nötig:** per-Provider-Flag **`authoritative-for-profile`** — heute patcht *jeder* Provider bei *jedem* Login (`ApplyUserUpdatesAsync:244-332`, last-writer-wins, Flapping real). Künftig: nur Provider mit Flag schreiben Profil. Default: der JIT-erstellende Provider wird authoritative. (Prior-Art: Entra „source of authority", Ping „authoritative IdP".)

### Neue Flags
- **`LoginProvider.TrustForAuthorization`** (spiegelt `TrustForEmailLink` `:117`). Nicht-getrustete Provider treiben keine privilegierte Membership. Default **false**.
- **`LoginProvider.AuthoritativeForProfile`** (siehe oben). Default: JIT-Ersteller.
- **`Group.ExternallyDrivable`** (Authorization-Slice). Nur Opt-in-Gruppen werden aus externen Claims berechnet. Default **false**.
- `LoginProvider.Slug` (`:52`, immutabel) = das `source=provider:<slug>`-Tag.

## Login-Pipeline (auf echte Seams)

```
ProcessAsync (ExternalLoginProcessor.cs:40)  ── OIDC (ExternalAuthEndpoints:144) + SAML (SamlLoginFlow:202)
  ├─(1) Capture+Tag   ExtractRawClaims (:506) → Claims mit source=provider:<slug> (⚠ heute wird Issuer verworfen)
  ├─(2) Transform     scriptRunner.Run (:79) → ResolvedClaims (nur claims) (⚠ MapToPatch :84-104 widen/2. Stufe)
  ├─(3) Persist       Claims-Store: DELETE source=provider:<X> + WRITE frisch (+capturedAt)
  │                   Profil-Patch getrennt, nur wenn Provider AuthoritativeForProfile
  ├─(4) Membership
  │      Durable → bestehender AutoMembershipRecalculator über Person; v1-Filter source=local → schreibt MemberIds
  │      Session → NEU ILoginTimeMembershipDeriver (Authorization): in-memory über (local ∪ provider:<X>),
  │               nur ExternallyDrivable-Gruppen, MembershipEvaluator.BuildPredicate (:16), KEIN MemberIds-Write
  ├─(5) Bake-in       Gruppen-IDs als INTERNER No-Destination-Claim auf die sign-in ClaimsIdentity (Success():334)
  ├─(6) AuthLog       logger.LogInformation("Auth: …") pro privilegiertem externen Grant
  ▼
Auflösungs-Zeit (UserInfo human / Token M2M):  BuildResourceAccessAsync (:556)
   → union(persisted MemberIds, session-derived aus dem Grant) → expand → nur Modgud-Rollen/Permissions
```

## Der Zwei-Ebenen-Filter (Sicherheits-Angel)

Membership aus den Claims, mit zwei Source-Sichten:
1. **Durable/enumerierbar** (`MemberIds`): **v1 = nur `source=local`** → externe Claims treiben nie *durable* Membership → keine Staleness. **v2 = Filter weg.**
2. **Live-Session** (was *dieser* Login ins Grant bekommt): **`source=local` ∪ `source=provider:<aktueller Login-Provider>`** — **nicht** alle persistierten Provider.

Warum (2) so streng: ein **Passwort**-Login darf die persistierten `provider:entra`-Claims **nicht** aufgreifen — sonst trägt er EntraID-Admin = Stale-Admin-Falle. Passwort-Login → nur lokal. ✅

## D — Token-Carrier {#d-token-carrier}

Die session-derived Gruppen-IDs reisen als **interner No-Destination-Claim**: gesetzt am Cookie (`Success()`), bei `/connect/authorize` von `CreateClaimsPrincipalAsync (:836)` mit leerer Destination in den Grant kopiert → OpenIddict persistiert, **emittiert nie**. Bei UserInfo/Token rekonstruiert OpenIddict den Principal inkl. interner Claims → `BuildResourceAccessAsync` unioned die IDs in die Gruppen-Menge *vor* der Expansion. **Der RS sieht die IDs nie** (Hub-Regel: keine Gruppen im Token/UserInfo). Derselbe Cookie-Claim bedient Modguds eigene `RequiresPermission`-Authz (gemeinsamer Union-Punkt in `PermissionService`). **Setzt Reference-Tokens voraus** (Default) — bei JWT-Access-Token-Clients fehlt der interne Claim bei UserInfo; das ist die v1-Grenze (dieselben Clients mit dem Hotfix-C-Restfenster).

## E — Session = Lease {#e-session--lease}

**Extern-abgeleitete Authz ist gültig, solange die Session/das Grant gültig ist — Punkt.** Sie endet mit der Session: Cookie-Ablauf, Logout, Refresh-Ablauf, oder Stamp-Rotation (Hotfix C: Deactivate/Delete → kill). Refresh leitet **nicht** neu her (keine Upstream-Claims); Re-Derivation nur bei frischem interaktivem Provider-Login.

**Bewusst VERWORFEN: ein capturedAt-Drop-Timer mitten in der Session.** Begründung (Design-Dialog): man darf nur auf **Evidenz** widerrufen, nicht auf **Annahme**. Bei „capturedAt > X wegwerfen" weiß Modgud nicht, ob der User die Claims noch hat — die 99% intakten User würden bestraft (mid-session „halbe App"), das bricht die SSO-Erwartung und das OAuth-Vertrauensmodell (gültiger Cookie ⇒ gültiger Zugriff).

**Willst du straffer:** (1) per-Realm kürzere Cookie-/Refresh-TTL + Sliding aus (ehrlich — der User *weiß*, dass er sich neu einloggt = frische Evidenz); (2) **SCIM (v2)** — evidenzbasierter Out-of-band-Widerruf. `capturedAt` wird gespeichert (Was-wäre-wenn + v2-Lease), in v1 aber nicht durchgesetzt.

**TTL-Defaults bleiben** (Access 60 min / Refresh 14 d / Cookie 30 d sliding), werden **per-Realm konfigurierbar** + dokumentiert. Pre-1.0 → später anpassbar. v1 bleibt fail-closed im entscheidenden Sinn: ephemer, nichts persistiert als stehender Grant über die Session hinaus; heilt sich bei Session-Ende/Re-Login.

## v1 vs v2

| | v1 (gemischt) | v2 (Realm-Modus „ein Provider") |
|---|---|---|
| Mehrere Provider / lokale User | erlaubt | verboten (Single-Provider-Gate) |
| Durable-Membership-Filter | `source=local` | **kein Filter** (alle Sources) |
| Externe Gruppen | session-scoped (ephemer) | durable + **enumerierbar** |
| Enumeration „wer ist drin?" | nur lokale Member | alle |
| Stale-Admin | ausgeschlossen (Zwei-Ebenen-Filter) | sicher (kein alternativer Login-Pfad) |
| Neuer Code ggü. v1 | — | nur Filter weg + Single-Provider-Gate (additiv) |

## Guardrails

- **`realm:admin` local-only — hart erzwungen** (externe Claims = untrusted Input): eine `realm:admin`-tragende Gruppe kann **nicht** `ExternallyDrivable` sein (bidirektionaler Config-Guard) + defensiver Strip in `ExpandBypassTiers (:~627)`. `app:admin` und darunter dürfen extern getrieben werden (gated durch `TrustForAuthorization` + `ExternallyDrivable`). **Best-Practice (nicht hart geblockt):** `realm:admin`-Gruppen manuell verwalten statt per Script — UI-Hinweis, kein Hard-Block für lokale Auto-Scripts.
- **`source`-Tag im Script sichtbar** — damit ein Script „nur EntraID-Gruppen" ausdrücken kann (v1: Script scopt selbst; deklaratives per-Provider-Group-Scoping später).
- **Keine rohen Upstream-Claims / Gruppen im Token/UserInfo** (Hub-Grenze).
- **Zwei Engines müssen übereinstimmen** (SQL-Batch vs. in-memory): Eval-Principal-Hydration (NormalizedEmail UPPER, null vs. leer) exakt wie persistierte Person; Reconciliation-Test pflicht.
- **PII:** Claims-Store in GDPR/Delete mit-scrubben.

## Beschlossen (A–G, Design-Dialog 2026-05-29)

- **A** — Profil-Patch getrennt + neues `AuthoritativeForProfile`-Flag (heute kein Gate).
- **B** — *ein* einheitlicher Claims-per-Source-Store (kein synthetischer Login-only-Typ); `source` + `capturedAt`.
- **C** — neuer `ILoginTimeMembershipDeriver` (Authorization), evaluate-only, teilt nur `IMembershipEvaluator`/EvaluateSafe-Logik.
- **D** — interner No-Destination-Claim, Cookie→Grant, Union in `BuildResourceAccessAsync`; Reference-Tokens vorausgesetzt.
- **E** — Session = Lease wörtlich; capturedAt-Timer-Drop verworfen; TTL-Defaults bleiben + per-Realm konfigurierbar.
- **F** — v1 = Script-liest-`claims`; explizite `extgroup→group`-Mapping-Tabelle später.
- **G** — `realm:admin` hart local-only (Config-Guard + Strip) + Best-Practice manuell; `source` sichtbar; Provider-Scoping via Script (a).
- **v1↔v2** — eine Codebasis, Unterschied = Source-Filter + Single-Provider-Gate.

## Implementierungs-Berührungspunkte (für den Plan)

`ExternalLoginProcessor.cs` (Capture+Tag :506; Transform-Call :79; neue Schritte 3-6; `Success()` :334; `ExternalLoginResult` :529) · `UserUpdateScriptRunner.cs` (ResolvedClaims-Output :150, MapToPatch :84) · neuer `ILoginTimeMembershipDeriver` (Authorization) · `AutoMembershipRecalculator.cs` (EvaluateSafe :164 als geteilte Logik) · neuer Claims-Store-Doc + Marten-Schema + Masking-Rule + GDPR/Delete-Cascade · `LoginProvider` (+`TrustForAuthorization`, +`AuthoritativeForProfile`, Events+API+UI) · `Group` (+`ExternallyDrivable`) · `AuthorizationEndpoints.cs` (`CreateClaimsPrincipalAsync` :836 internen Claim setzen; `BuildResourceAccessAsync` :556 extra-IDs; `ExpandBypassTiers` :~627 realm:admin-Strip) · `PermissionService` (Union-Overload) · Config-Guard realm:admin↔ExternallyDrivable · Reconciliation-Test (zwei Engines).

## Bewusst NICHT in v1 (additiv später)

v2-Realm-Modus (Single-Provider durable + enumerierbar) · `groups`-Scope auf UserInfo (opt-in, schöpft aus derselben effektiven Gruppen-Menge) · deklaratives per-Group-Provider-Scoping · explizite `extgroup→group`-Mapping-Tabelle · SCIM-Inbound + scheduled Pull · Lease/Decay via *Reconciliation* (nicht Timer; v2, nutzt `capturedAt`) · Revocation-Epoch-Check für JWT-Access-Tokens (Hotfix-C-Restfenster).

## Herkunft

Integrations-Map `wf_63933d9f-149` (6 Agenten, read-only). Roh-Output `.local/wf4-map.txt`. Entscheidungen A–G im Spec-Dialog 2026-05-29 beschlossen.
