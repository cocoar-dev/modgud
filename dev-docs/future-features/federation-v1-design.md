# Federation v1 — Implementierungs-Spec

Status: **Design-Entwurf zur Review** (kein Code). Konkretisiert das in [identity-lifecycle-untangle](./identity-lifecycle-untangle#federation-prior-art) beschlossene v1-Modell zu echten Code-Seams. Erstellt 2026-05-29 nach einem 6-Agenten-Integrations-Mapping (`wf_63933d9f-149`). **Am Ende steht eine Liste „Entscheidungen für dich" — die gehören noch dir, ich habe sie nicht still entschieden.**

> **Erinnerung an das beschlossene Modell:** Hub-vs-Federation ist kein Realm-*Modus*, sondern eine Eigenschaft der Herleitung *jeder einzelnen Mitgliedschaft*. v1 ist **pure-ephemer, session-scoped**: externe Claims werden beim Login gelesen, transformiert, gegen die Membership-Scripts evaluiert, das Ergebnis lebt nur in Session/Token (nie in `Group.MemberIds`). **Die Session ist der Lease.** Identität bleibt strikt Hub. Sicherheit sitzt in Per-Provider-Trust + Per-Gruppe-Opt-in + Audit, `realm:admin` local-only.

## Der entscheidende Architektur-Befund

Zwei Fakten aus dem Code prägen das ganze Design:

1. **Ein einziger Seam.** OIDC *und* SAML konvergieren auf **`ExternalLoginProcessor.ProcessAsync`** (`ExternalLoginProcessor.cs:40-237`). OIDC ruft es aus `ExternalAuthEndpoints.cs:144`, SAML aus `SamlLoginFlow.cs:202`. Die Federation-Logik wird **einmal** geschrieben, nicht pro Protokoll. SAML braucht *keine* Änderung — die Gruppen/Rollen-Attribute kommen im konvergierten Principal bereits an (`SamlLoginFlow.BuildExternalPrincipal:293-345`), sie werden heute nur nicht *konsumiert*.

2. **Autorisierung wird SPÄT aufgelöst, nicht beim Login.** Cookie/Session tragen heute **null Authz** (`Success()` baut bewusst eine authz-freie `ClaimsIdentity`, `ExternalLoginProcessor.cs:334-365`). Rollen/Permissions werden erst zur **Token-Ausgabe** berechnet: `AuthorizationEndpoints.BuildResourceAccessAsync:556-597` → `IPermissionService` → BFS über **persistierte `Group.MemberIds`** (`PermissionService.cs:97-126`). Heißt: die session-abgeleiteten Memberships müssen (a) beim Login berechnet, (b) auf dem Cookie-Principal getragen und (c) bei der Token-Ausgabe mit dem persistierten Pfad **vereinigt** werden. Das ist die Cross-Area-Naht des ganzen Features.

## Die Pipeline, auf echte Seams gemappt

```
ProcessAsync (ExternalLoginProcessor.cs:40)  ── OIDC (ExternalAuthEndpoints:144) + SAML (SamlLoginFlow:202)
  │
  ├─(1) Claim-Capture     ExtractRawClaims (:506) → Claims source-getaggt (provider:<slug> | local)
  │                        ⚠ heute wird Claim.Issuer verworfen → Provenance muss rein
  ├─(2) Transform         scriptRunner.Run (:79) — UserUpdateScript als Transform-Stufe
  │                        ⚠ MapToPatch (:84-104) droppt heute alles außer 4 Feldern
  ├─(3) In-Memory-Eval    NEU: synthetischer Person-Principal (local ∪ provider) →
  │                        MembershipEvaluator.BuildPredicate<Principal>(:16).Compile().Invoke()
  │                        Muster wie AutoMembershipRecalculator.EvaluateSafe (:164-178)
  │                        ABER: KEIN GroupMembershipRecomputedEvent (nie MemberIds schreiben)
  │                        nur Gruppen mit Per-Gruppe-Opt-in "externally-drivable"
  ├─(4) Bake-in-Session   Success() (:334-365) → computed memberships als Claim auf die
  │                        sign-in ClaimsIdentity; ExternalLoginResult (:529) trägt sie
  │                        → SignInAsync (ExternalAuthEndpoints:155) persistiert sie im Cookie
  ├─(5) AuthLog           logger.LogInformation("Auth: …") pro privilegiertem Grant (AuthLogService:21)
  ▼
Token-Zeit:  BuildResourceAccessAsync (:556-597) ── NEU: union(session-derived, persisted MemberIds)
             weiterhin nur Modgud-Rollen/Permissions emittiert, NIE rohe Upstream-Claims
```

Sauberste Hook-Form: ein neuer Collaborator **`ILoginTimeMembershipDeriver`**, injiziert in `ExternalLoginProcessor`, aufgerufen zwischen Schritt (2) und `Success()`. Er kapselt Schritt (3) und hält den Recalculator-Pfad (persist) unberührt.

## Datenmodell-Kernentscheidung: die `externalClaims`/`externalGroups`-Fläche ist *login-only*

Das ist der subtilste Punkt. Das Membership-Script liest heute `Person` (`Person.cs:11-47`) — stark typisiert (Firstname/Email/Acronym/ExternalIdentities), **kein generischer Claims-Bag**. Damit ein Script auf EntraID-Gruppen matchen kann, braucht der Eval-Input eine `externalGroups`/`externalClaims`-Fläche.

**Vorschlag:** Diese Fläche existiert **ausschließlich auf dem synthetischen Login-Zeit-Principal**, **nie auf dem persistierten `Person`-Dokument.** Folgen, die das elegant macht:

- Der **Batch-Recalculator** (Postgres-JSONB über persistiertes `Person`) sieht `externalGroups` nie → ein Script, das `p.externalGroups.includes(...)` referenziert, matcht **im Batch nie** und **nur beim Login** (synthetischer Principal hat die Fläche). Damit ist „externe Gruppe treibt Membership" **by construction login-only und ephemer** — genau das Modell, ohne extra Durchsetzung.
- Die Doku-Leitplanke „beide Engines müssen übereinstimmen" verfeinert sich zu: *sie stimmen auf den lokalen Feldern überein; `externalGroups` ist absichtlich login-only.* Kein Persistieren eines login-snapshot → keine Stale-Admin-Falle.

Das bedingt eine kleine Erweiterung des Principal-Typs (oder ein Login-only-Subtyp), den `BuildPredicate<Principal>` akzeptiert. Form siehe Entscheidung **B**.

## Neue Konfiguration

- **Per-Provider `TrustForAuthorization`** (neues Feld auf `LoginProvider`, spiegelt exakt `TrustForEmailLink` `LoginProvider.cs:117` — Event + Admin-API + UI). Claims eines nicht-getrusteten Providers treiben **keine** privilegierte Membership. Default: **false** (fail-safe).
- **Per-Gruppe „externally-drivable"-Opt-in** (neues Feld auf dem `Group`-Aggregat, Authorization-Slice). Nur opt-in-Gruppen laufen in Schritt (3). Default: **false**.
- `Slug` (`LoginProvider.cs:52`, immutabel) wird das `source=provider:<slug>`-Tag.

## Guardrails (Sicherheit sitzt hier)

- **`realm:admin` local-only:** Federation-abgeleitete Membership darf den Realm-Emergency-Tier nie verleihen. Durchsetzung **zweifach**: (a) Schritt-(3)-Eval filtert realm-admin-tragende Gruppen raus, (b) defensiv in `ExpandBypassTiers` (`AuthorizationEndpoints.cs:~627`, behandelt `realm:admin` schon gesondert).
- **Keine rohen Upstream-Claims im Token:** die Transform-Widening (Schritt 2) darf keine beliebigen Script-Keys in `BuildResourceAccessAsync` durchbluten — Output bleibt strikt Modgud-Rollen/Permissions.
- **Provider-Trust + Gruppen-Opt-in + Audit** sind die eigentliche Kontrolle (nicht Tabelle-vs-Script).

## Entscheidungen für dich (die gehören dir, nicht still entschieden)

**A — Transform-Contract: bestehendes `UserUpdateScript` widen, oder zweites Script-Feld?** Heute gibt `(claims)=>({firstname,lastname,email,acronym})` zurück; `MapToPatch` droppt den Rest. Optionen: (a) `UserUpdateResult` widen, sodass dasselbe Script zusätzlich einen `claims`/`groups`-Output liefert; (b) ein zweites `LoginProvider`-Feld `ClaimTransformScript` (Trennung Profil-Patch vs. Authz-Claims). → *Empfehlung: (b)* — saubere Trennung, das Profil-Patch-Script bleibt unverändert, und „dieses Script kann Privileg beeinflussen" ist explizit ein eigenes, separat reviewbares Artefakt.

**B — Form des synthetischen Principals + `externalGroups`-Fläche.** `Person` hat keinen Claims-Bag. Optionen: (a) ein Login-only-Subtyp `FederatedPrincipal : Principal` mit `Dictionary<string,string[]> ExternalClaims` + `string[] ExternalGroups`, den `BuildPredicate<Principal>` akzeptiert; (b) `Person` selbst um eine `[NotPersisted]`-Fläche erweitern (riskanter — könnte versehentlich persistiert werden). → *Empfehlung: (a)* — der separate Typ macht „nur beim Login vorhanden" strukturell unmöglich zu persistieren.

**C — Wo lebt die Login-Zeit-Eval?** (a) neuer `ILoginTimeMembershipDeriver`; (b) `AutoMembershipRecalculator` um eine non-persisting `EvaluateOnly`-Methode erweitern. → *Empfehlung: (a)* — hält den Recalculator-Persist-Pfad sauber; teilt nur die `EvaluateSafe`-Compile/Invoke-Logik (`AutoMembershipRecalculator.cs:164-178`).

**D — Token-Carrier: wie erreichen die session-abgeleiteten Memberships `BuildResourceAccessAsync`?** Der Cookie ist der Träger, aber `BuildResourceAccessAsync` ignoriert heute die Cookie-Claims und re-derived aus persistierten `MemberIds`. Optionen: (a) **Gruppen-IDs** (kompakt) als Claim in den Cookie backen → `BuildResourceAccessAsync` unioned sie mit dem persistierten BFS; (b) abgeleitete Memberships server-seitig an die `UserSession`-Row hängen (kein Cookie-Bloat, aber neue Lese-Kopplung). → *Empfehlung: (a) mit Gruppen-IDs, NICHT expandierten Permissions* — kompakt, und die Expansion (Gruppe→Rolle→Permission) passiert wie gehabt zur Token-Zeit. Siehe Risiko Cookie-Größe.

**E — Refresh-Token-Pfad.** Refresh (`AuthorizationEndpoints.cs:263-303`) re-mintet *ohne* erneuten OIDC-Login → kann die Membership nicht neu herleiten (keine Upstream-Claims zur Hand). Optionen: (a) refreshtes Token trägt die beim Login eingefrorenen Memberships weiter — Lease = Cookie-Session-TTL, Refresh-Token an die Session gebunden; (b) Access-Token-TTL kurz halten + Refresh an die lebende Cookie-Session koppeln, sodass der Verfall der Session den Grant beendet. → *Empfehlung: (b)* — kurze Access-Token-TTL + Refresh nur gültig, solange die Cookie-Session lebt; dann ist „Session = Lease" wirklich der Boden. **Braucht deine Bestätigung der TTL-Werte.**

**F — Mapping-Tabelle vs. Script-only für v1.** Das beschlossene Modell nannte die explizite `extgroup→Modgud-group`-Tabelle als *primär* und Script-über-`externalGroups` als sekundär. Der Code hat heute nur die Script-Engine. Optionen: (a) v1 = nur Script-liest-`externalGroups` (kleinste Codemenge, nutzt die bestehende Engine); (b) v1 = explizite Mapping-Tabelle (auditierbarer, aber net-new Aggregat + UI); (c) beide. → *Empfehlung: (a) für v1*, Mapping-Tabelle als additives Zucker später — die Script-Engine existiert schon, und die Audit-Lücke wird durch das Grant-beim-Login-AuthLog (Schritt 5) gemildert.

**G — `realm:admin`-Guard-Ort + Provenance im Script-Input.** Bestätige: realm-admin-Filter in Schritt (3) *und* defensiv in `ExpandBypassTiers`; und ob das Transform-Script das `source`-Tag sehen soll (sinnvoll, damit ein Script „nur EntraID-Gruppen" ausdrücken kann).

## Risiken (ins Bauen mitnehmen)

- **Zwei-Engine-Divergenz:** der synthetische Principal muss bei Feld-Hydration (v.a. `NormalizedEmail` UPPER, null vs. leer) exakt der persistierten `Person` entsprechen, sonst klassifiziert derselbe User je nach Pfad anders. Reconciliation-Test pflicht (steht schon als Item in der untangle-Doku).
- **Cookie-Größe:** Memberships in den Cookie backen → Bloat/Chunking (heute trägt der Cookie 5 Claims). Entscheidung D(a) mit Gruppen-IDs hält es klein; ggf. Schwelle + Fallback auf D(b) server-seitig.
- **`realm:admin`-Eskalation:** ohne den Guard wäre ein fälschbarer Upstream-Gruppen-Claim → realm:admin ein Lockout-Bypass. Doppelte Durchsetzung nicht optional.
- **Alle Success-Branches:** die Eval muss in *jedem* Branch laufen (returning-link `:142`, link-to-current-user `:165`, email-link `:198`, JIT `:236`) — leicht, sie nur in einen zu verdrahten und Federation-Membership beim JIT-First-Login still zu überspringen.
- **SameSite=Lax-Link-Flow** (SAML, `SamlLoginFlow.cs:185-193`): unverändert, aber relevant — der Link-Flow degradiert weiterhin zu JIT/email-auto-link; Federation-Membership läuft trotzdem (hängt nur am resolved user).

## Bewusst NICHT in v1 (additiv später)

Persistierte/durable externe Membership + Lease-Sweep (das „durable-mit-Lease"-Ende); gespeicherte Per-Source-Claim-Snapshots für Was-wäre-wenn; explizite `extgroup→group`-Mapping-Tabelle als Zucker; SCIM-Inbound + scheduled Pull; Revocation-Epoch-Check für JWT-Access-Tokens (der Rest aus dem Hotfix-C-Restfenster).

## Herkunft

Integrations-Map: Workflow `wf_63933d9f-149` (6 Agenten, ~697k Tokens, read-only). Roh-Output unter `.local/wf4-map.txt` für diese Session. Baut auf [identity-lifecycle-untangle](./identity-lifecycle-untangle) auf; setzt Hotfix C (PR #21, `766c9f8`) voraus (Revocation-Infra, auf die der Session-Lease-Boden zählt).
