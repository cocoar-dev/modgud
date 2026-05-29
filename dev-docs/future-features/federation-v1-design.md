# Federation v1 — Implementierungs-Spec

Status: **Design (zur Review), kein Code.** Konkretisiert das in [identity-lifecycle-untangle](./identity-lifecycle-untangle#federation-prior-art) beschlossene Federation-Modell zu echten Code-Seams. Grundlage: Integrations-Map `wf_63933d9f-149` (2026-05-29). Setzt Hotfix C (PR #21, `766c9f8`) voraus — die Revocation-Infra, auf die der Session-Lease zählt.

> **Hintergrund** (Prior-Art, Stale-Admin-Falle, Hub-vs-Broker-Spektrum): siehe das [Untangle-Doc](./identity-lifecycle-untangle). Dieses Doc ist die *Bau-Vorlage*.

## Das Modell in einem Absatz

Hub-vs-Federation ist kein Realm-*Modus*, sondern eine Eigenschaft der Herleitung *jeder einzelnen Mitgliedschaft*. Externe Claims werden bei jedem Login gelesen, transformiert und **mit `source`-Tag + Timestamp auf einem Claims-Store an der Person persistiert** (der aktuelle Provider wird dabei delete+rewrite refresht). Gruppen-Membership wird aus diesen Claims berechnet — durch **eine** Engine, gesteuert durch **einen Source-Filter**. Identität bleibt strikt Hub (Token trägt nur Modgud-Rollen/Permissions, nie rohe Upstream-Claims). **Die Session ist der Lease.**

**v1 (gemischt: mehrere Provider / lokale User erlaubt)** und **v2 (Realm-Modus „genau ein externer Provider")** sind *eine* Codebasis; sie unterscheiden sich nur durch den Source-Filter (siehe [v1 vs v2](#v1-vs-v2)).

## Zwei Architektur-Befunde, die alles prägen

1. **Ein einziger Seam.** OIDC *und* SAML konvergieren auf **`ExternalLoginProcessor.ProcessAsync`** (`ExternalLoginProcessor.cs:40-237`) — OIDC aus `ExternalAuthEndpoints.cs:144`, SAML aus `SamlLoginFlow.cs:202`. Die Federation-Logik wird **einmal** geschrieben. SAML braucht keine eigene Änderung (Gruppen/Rollen-Attribute kommen im konvergierten Principal bereits an, `SamlLoginFlow.cs:293-345`).

2. **Autorisierung wird SPÄT aufgelöst.** Cookie/Session tragen heute *null* Authz (`Success()` baut bewusst eine authz-freie `ClaimsIdentity`, `ExternalLoginProcessor.cs:334-365`). Rollen/Permissions entstehen erst zur Token-Ausgabe: `AuthorizationEndpoints.BuildResourceAccessAsync:556-597` → `IPermissionService` → BFS über persistierte `Group.MemberIds` (`PermissionService.cs:97-126`). Folge: session-abgeleitete Memberships müssen bei der Token-Ausgabe mit dem persistierten Pfad **vereinigt** werden.

## Datenmodell

### Claims-per-Source-Store (neu)
Ein per-User persistierter Store (eigenes Marten-Dokument, keyed auf `userId` — wie `UserSession`, nicht event-sourced, weil es refresh-bare Snapshot-Daten sind), der eine flache Liste von Claim-Einträgen hält:

```
ClaimEntry { source, type, value, capturedAt }
  source     = "local" | "provider:<slug>"
  type       = Claim-Name (z.B. "groups", "department", "email")
  value      = string | string[]
  capturedAt = Zeitpunkt dieses Logins   ← für Was-wäre-wenn-Alter, künftiges Lease/Decay, Staleness-Anzeige
```

- **Lokale Identität** lebt weiterhin auf der `Person` (getypte Felder Firstname/Email/Acronym/…) — sie ist `source="local"`. Diese Felder werden zur Eval-Zeit in die Claims-Sicht projiziert, **nicht** dupliziert.
- **Externe Claims** (`source="provider:<slug>"`) kommen in den Store. **Bei jedem Login wird `source="provider:<X>"` (X = der gerade benutzte Provider) komplett gelöscht und neu geschrieben** (SET/FORCE-Reconcile auf Claim-Ebene). Lokale Claims und *andere* Provider bleiben unberührt.
- **PII-Pflicht:** der Store trägt PII (Email, rohe Claims) → **GDPR-Erase + Delete-Pfade müssen ihn mit-scrubben** (Masking-Rule + Delete, exakt das Muster aus Hotfix C für `ExternalIdentityLink`).

### Transform-Ausgabe: standardisiertes `ResolvedClaims`
Jeder Provider-Login produziert über die Transform-Stufe (das heutige `UserUpdateScript`-Engine, `UserUpdateScriptRunner`) ein **standardisiertes Objekt mit *nur* Claims** — kein separates `groups`. „Groups" ist Provider-Vokabular; EntraID-Gruppen sind einfach ein `claims.groups`-Eintrag, SAML-`memberOf` ein anderer. Das Transform-Script normalisiert/berechnet Claims (z.B. GUIDs → lesbare Gruppennamen, `fullName` aus first+last). Alle nachgelagerten Scripts lesen danach uniform `claims`.

### Profil-Patch: getrennt, mit Authoritative-Gate (neu)
Die 4 Profilfelder (firstname/lastname/email/acronym) bleiben **first-class auf dem Modgud-User** und werden **getrennt** vom Claims-Store gepatcht (ein dünner Schritt liest well-known Claims → User-Felder). **Neu nötig:** ein per-Provider-Flag **`authoritative-for-profile`** — heute patcht *jeder* Provider bei *jedem* Login (last-writer-wins, Flapping-Risiko real). Künftig: nur Provider mit dem Flag schreiben Profil. Default: der Provider, der den User per JIT anlegt, wird authoritative; andere authentifizieren, fassen das Profil nicht an. (Prior-Art: Entra „source of authority", Ping „authoritative IdP". Später optional Okta-Style-Prioritätsliste.)

### Neue Flags
- **Per-Provider `TrustForAuthorization`** (auf `LoginProvider`, spiegelt `TrustForEmailLink` `LoginProvider.cs:117`). Claims eines nicht-getrusteten Providers treiben keine privilegierte Membership. Default **false**.
- **Per-Provider `authoritative-for-profile`** (siehe oben). Default: JIT-Ersteller.
- **Per-Gruppe „externally-drivable"-Opt-in** (neues Feld auf dem `Group`-Aggregat, Authorization-Slice). Nur Opt-in-Gruppen werden überhaupt aus externen Claims berechnet. Default **false**.
- `Slug` (`LoginProvider.cs:52`, immutabel) wird das `source=provider:<slug>`-Tag.

## Login-Pipeline (auf echte Seams gemappt)

```
ProcessAsync (ExternalLoginProcessor.cs:40)  ── OIDC (ExternalAuthEndpoints:144) + SAML (SamlLoginFlow:202)
  │
  ├─(1) Capture+Tag    ExtractRawClaims (:506) → Claims mit source=provider:<slug>
  │                     ⚠ heute wird Claim.Issuer verworfen → Provenance muss rein
  ├─(2) Transform      scriptRunner.Run (:79) → standardisiertes ResolvedClaims (nur claims)
  │                     ⚠ MapToPatch (:84-104) droppt heute alles außer 4 Feldern → widen/zweite Stufe
  ├─(3) Persist        Claims-Store: DELETE source=provider:<X> + WRITE frisch (mit capturedAt)
  │                     Profil-Patch getrennt, nur wenn Provider authoritative-for-profile
  ├─(4) Membership-Eval
  │       Durable  → bestehender AutoMembershipRecalculator über Person (source=local)
  │                  schreibt MemberIds — in v1 sieht er NUR lokale Claims (Filter)
  │       Session  → NEU: in-memory über (local ∪ provider:<X>), nur externally-drivable Gruppen,
  │                  MembershipEvaluator.BuildPredicate (:16) — KEIN MemberIds-Write
  ├─(5) Bake-in        computed session-memberships als Claim auf die sign-in ClaimsIdentity (Success():334)
  ├─(6) AuthLog        logger.LogInformation("Auth: …") pro privilegiertem externen Grant
  ▼
Token-Zeit:  BuildResourceAccessAsync (:556) → union(persisted MemberIds, session-derived)
             weiterhin nur Modgud-Rollen/Permissions, NIE rohe Upstream-Claims
```

## Der Zwei-Ebenen-Filter (die Sicherheits-Angel)

Membership wird aus den Claims gerechnet — aber mit **zwei** unterschiedlichen Source-Sichten:

1. **Durable / enumerierbar** (was in `MemberIds` landet, „wer ist in Gruppe X"): **v1 = nur `source=local`**. → externe Claims treiben nie *durable* Membership → können nicht veralten. **v2 = Filter weg** (alle Sources).
2. **Live-Session** (was *diese* Anmeldung ins Token bekommt): **`source=local` ∪ `source=provider:<dem-gerade-benutzten-Provider>`** — und **nicht** alle persistierten Provider.

Warum (2) so streng sein muss: Logge ich mich per **Passwort** ein, darf die Session **nicht** die persistierten `provider:entra`-Claims aufgreifen — sonst trägt die Passwort-Anmeldung EntraID-abgeleitetes Admin = **Stale-Admin-Falle**. Passwort-Login → kein aktueller externer Provider → nur lokal → kein externes Admin. ✅

## v1 vs v2

| | v1 (gemischt) | v2 (Realm-Modus „ein Provider") |
|---|---|---|
| Mehrere Provider / lokale User | erlaubt | verboten (Single-Provider-Gate) |
| Durable-Membership-Filter | `source=local` | **kein Filter** (alle Sources) |
| Externe Gruppen | session-scoped (ephemer) | durable + **enumerierbar** |
| Enumeration „wer ist drin?" | nur lokale Member | alle |
| Stale-Admin | ausgeschlossen (Zwei-Ebenen-Filter) | sicher, weil kein alternativer Login-Pfad |
| Neuer Code ggü. v1 | — | nur: Filter weg + Single-Provider-Gate (rein additiv) |

**v2 ist rein additiv** — gleiche Pipeline, gleicher Store, gleiche Engine; nur der Durable-Filter entfällt und der Realm erzwingt „genau ein externer Provider, keine lokalen Passwort-User für föderierte". Kein Umbau von v1.

## Guardrails

- **`realm:admin` local-only:** federation-abgeleitete Membership darf den Realm-Emergency-Tier nie verleihen. Doppelt durchsetzen: (a) im Membership-Eval (Schritt 4) realm-admin-tragende Gruppen rausfiltern, (b) defensiv in `ExpandBypassTiers` (`AuthorizationEndpoints.cs:~627`).
- **Keine rohen Upstream-Claims im Token:** Transform-Output speist nur Membership; Token bleibt strikt Modgud-Rollen/Permissions.
- **Zwei Engines müssen übereinstimmen:** der Eval-Principal (Person + Claims-Sicht) muss bei Feld-Hydration (NormalizedEmail UPPER, null vs. leer) exakt der persistierten Person entsprechen, sonst klassifiziert derselbe User je nach Pfad anders. Reconciliation-Test pflicht.
- **PII:** Claims-Store in GDPR-Erase + Delete mit-scrubben.

## Beschlossen (in diesem Design-Dialog)

- **A** — Profil-Patch getrennt + neues `authoritative-for-profile`-Flag (heute *kein* Gate → Flapping real).
- **B** — *ein* einheitlicher Claims-per-Source-Store (kein synthetischer Login-only-Typ). Claims haben `source` + `capturedAt`.
- **F** — v1 = Script-liest-`claims` (bestehende Engine); explizite `extgroup→group`-Mapping-Tabelle erst später als Zucker.
- **Persist-in-v1** — externe Claims schon in v1 mitspeichern (winziger v2-Delta + Was-wäre-wenn gratis; GDPR-Scrub-Pflicht).
- **v1↔v2** — eine Codebasis, Unterschied = Source-Filter + Single-Provider-Gate.

## Offene Entscheidungen (gegen dieses Modell)

**C — Wo lebt die Login-Session-Eval?** (a) neuer `ILoginTimeMembershipDeriver`; (b) `AutoMembershipRecalculator` um eine non-persisting `EvaluateOnly`-Methode erweitern. → *Empfehlung: (a)* — hält den Recalculator-Persist-Pfad sauber, teilt nur die `EvaluateSafe`-Compile/Invoke-Logik (`AutoMembershipRecalculator.cs:164-178`).

**D — Token-Carrier für die session-derived Memberships.** (a) Gruppen-IDs (kompakt) in den Cookie backen → `BuildResourceAccessAsync` unioned mit dem persistierten BFS; (b) an die `UserSession`-Row hängen (kein Cookie-Bloat, neue Lese-Kopplung). → *Empfehlung: (a) mit Gruppen-IDs, nicht expandierten Permissions* (Expansion bleibt zur Token-Zeit). Cookie-Größe im Auge behalten.

**E — Refresh-Token-Pfad + TTL.** Refresh (`AuthorizationEndpoints.cs:263-303`) re-mintet ohne erneuten Login → kann nicht neu herleiten. → *Empfehlung: kurze Access-Token-TTL + Refresh nur gültig, solange die Cookie-Session lebt* (dann ist „Session = Lease" der echte Boden). **Braucht deine TTL-Werte.**

**G — `realm:admin`-Guard-Ort + `source`-Tag im Script sichtbar.** Bestätige: realm-admin-Filter in Schritt (4) *und* defensiv in `ExpandBypassTiers`; und ja, das Transform/Membership-Script soll das `source`-Tag sehen (damit ein Script „nur EntraID-Gruppen" ausdrücken kann).

## Bewusst NICHT in v1 (additiv später)

v2-Realm-Modus selbst (Single-Provider-durable) als nächster Schritt; explizite `extgroup→group`-Mapping-Tabelle als Zucker; SCIM-Inbound + scheduled Pull; Lease/Decay über `capturedAt` (das `capturedAt`-Feld bereiten wir aber jetzt schon vor); Revocation-Epoch-Check für JWT-Access-Tokens (Hotfix-C-Restfenster).

## Herkunft

Integrations-Map: Workflow `wf_63933d9f-149` (6 Agenten, read-only). Roh-Output `.local/wf4-map.txt`. Design-Entscheidungen A/B/F/persist/v1↔v2 im Spec-Dialog 2026-05-29 beschlossen.
