# Identity-Lifecycle Untangle

Status: **Analyse / Entscheidungs-Gate** (noch kein Code — das ist der „aufdröseln, bevor wir irgendwas anfassen"-Durchlauf vom 2026-05-28).

> **⚠️ Wurzel-Entscheidung TEILWEISE ÜBERHOLT (Klärung am selben Tag).** Die Reconciliation weiter unten empfiehlt „Hub-only ratifizieren und die `externalClaims`/`OrganizationalUnit`/`Department`-Beispiele aus `auto-membership.md` löschen". Der User hat dann klargestellt, dass Modgud **auch als Federation-Broker funktionieren muss**: eine App bindet nur Modgud an, das wiederum zum EntraID/Okta/SAML/LDAP des Tenants brokert — und in *einem* Realm **koexistieren beide Modi** (interne User per EntraID-SSO mit EntraID-Gruppen-gesteuerter Mitgliedschaft, externe User als lokale Passwort-Accounts). Damit ist Hub-only **verworfen**; die echte Positionierung ist **„Hub by default, Broker als per-Login-Provider-Opt-in"**, und jene Doku-Beispiele sind die **Spezifikation eines gewollten-aber-ungebauten Features**, nicht zu löschender Drift. Die harte Design-Arbeit ist die **Source-of-Truth + der Lifecycle extern-abgeleiteter Gruppenmitgliedschaften**, nicht Hub-vs-Proxy als Binärfrage. Die Kerngefahr ist die **Stale-Admin-Falle** (Gruppe upstream entfernt, aber der User loggt sich nie mehr über diese IdP ein → nicht widerrufbares Privileg). Ergänzung des Users: **SCIM ist KEIN ausreichendes Sicherheitsnetz** — push-basiert, verpasste Events werden nicht nachgesynct, kann deaktiviert sein oder brechen — daher muss das Modell **fail-closed** sein: extern-abgeleitete Grants müssen *verfallen*, wenn sie nicht aktiv re-bestätigt werden, statt per Default *zu bestehen*. **Siehe die neue Sektion [Federation Group-Sync: Prior Art + empfohlenes Modell](#federation-prior-art) am Ende dieser Seite** für die recherchierte Schlussfolgerung. Siehe Memory [[project-identity-lifecycle-untangle-2026-05-28]].

Diese Seite dröselt ein Cluster von Themen auf, deren Verkopplung der User richtig erahnt hat: den Account-Identity-Lifecycle-Folge-PR (Unlink-Tombstone, Email-Unique, zwei Lösch-Pfade), die Identity-Hub-vs-Federation-Proxy-Positionierung, Soft-Delete/Grace-Period, und „mehrere External-Logins pro User → wie passt das zum Gruppen-Membership-Script". Entstanden durch einen 10-Agenten-Mapping-Workflow (7 parallele Subsystem-Deep-Reads → 2 gegensätzlich gerahmte Dekompositionen → 1 adversariale Reconciliation). Jede tragende Behauptung unten wurde gegen echten Code mit `file:line`-Zitaten verifiziert; die wirkungsvollsten wurden zusätzlich von Hand re-verifiziert.

## Die sieben Themen (Graph-Knoten)

| ID | Thema |
|---|---|
| `HUBPROXY` | Identity-Hub vs Federation-Proxy-Positionierung (die philosophische Wurzel) |
| `EXTLOGIN` | External-Login-Identitätsmodell & Kardinalität |
| `EMAIL` | Email-Unique-Invariante & Matching-Key |
| `UNLINK` | Link / Unlink / Tombstone & Re-Link-Blocker (Variante C) |
| `DELETE` | Admin-Delete vs GDPR-Delete & PII-Handling |
| `SOFTDELETE` | Soft-Delete / Deaktivierung / Grace-Period |
| `MEMBERSHIP` | JsEval-Gruppen-Auto-Membership-Script-Inputs |

## Wie Identität heute tatsächlich funktioniert (das Fundament, auf dem alles aufbaut)

Es gibt zwei getrennte „Login"-Welten. **Lokale Faktoren** (Passwort, Passkey, Magic-Link, Email-OTP) werden *gar nicht* als External-Logins modelliert — `EventSourcedUserStore` implementiert nie `IUserLoginStore`. Der Passwort-Hash liegt auf `UserSecurityData` (1:1, `Id=UserId`); Passkeys sind separate `StoredPasskeyCredential`-Docs (1:N); Magic-Link/Email-OTP sind ephemere Challenges. **Föderierte Logins** (OIDC + SAML SP) werden durch das event-sourced Aggregat `ExternalIdentityLink` modelliert — ein Stream pro Link, Inline-Projektion. Natürlicher Schlüssel `(Issuer, Subject)`, global eindeutig über einen Marten-`UniqueIndex`. Kardinalität: ein User hält 0..n Links (1:N); ein gegebenes `(iss, sub)` mappt auf genau einen User (DB-erzwungen). SAML wird in denselben OIDC-förmigen `(iss/sub)`-Principal normalisiert, sodass beide Protokolle durch einen einzigen `ExternalLoginProcessor` laufen. Der autoritative Match-Key ist `(iss, sub)`; **Email ist nur ein Fallback** für opt-in Auto-Link/JIT.

Das ist ein **Hub**-Design: `ExternalLoginProcessor` stempelt nur Session-Mechanik-Claims, das `UserUpdateScript` erlaubt exakt vier Profilfelder (firstname/lastname/email/acronym), und rohe Upstream-Claims liegen isoliert auf `ExternalIdentityLink.LastRawClaims` und werden **nie in den User zurückgelesen**. Das Token/UserInfo emittiert nur Modgud-eigene Permissions + Rollen, niemals externe IdP-Claims.

## Wurzel-Entscheidung: `HUBPROXY` gated alles

Beide Dekompositions-Durchläufe — einer bottom-up vom Datenmodell, einer top-down von der Produkt-Positionierung — sind **unabhängig auf dieselbe Wurzel konvergiert**: `HUBPROXY` ratifizieren, bevor irgendwas am Datenmodell festgezurrt wird. Das ist das stärkstmögliche Signal, dass es das echte Gate ist.

Es ist die Wurzel, weil jeder nachgelagerte Fix *den Hub härtet*: Email zu einem DB-erzwungenen Identity-Key machen, `(iss,sub)` zum kanonischen Match-Key machen, externe Claims verworfen lassen, und den Membership-Script-Contract auf lokalen Feldern halten — all das ist **nur unter Hub-Semantik korrekt**. Bei einem künftigen Proxy/Hybrid-Flip würden Upstream-`sub`+Claims autoritativ, Email würde vom Identity-Key zum Komfort-Attribut degradieren, und der Membership-Script-Input-Contract müsste eine Claims-Fläche wachsen lassen — was schon-fertig-aussehende Arbeit entwertet.

Entscheidend: Die eigentlichen Lifecycle-*Fixes* sind im Memory bereits vor-entschieden (Variante C für Unlink; partial-unique-Email + Lösch-Pfad-Konvergenz). Die Gate-Frage ist also **nicht „was bauen" — sondern „dürfen wir das festzurren"**, und das beantwortet nur die `HUBPROXY`-Ratifizierung. `project_identity_hub_vs_federation_proxy_open.md` bestätigt, dass Hub in der Praxis entschieden, aber bewusst als *Produkt*-Diskussion offengehalten ist. Phase 0 pinnt es als **„Hub-only, cycle-scoped"** (kein permanenter Produktbeschluss) zum Preis eines einzigen Absatzes.

## Themen-Abhängigkeitsgraph

```
                 HUBPROXY  (Wurzel — zuerst entscheiden)
        ┌───────────┬───────────┬──────────┬─────────┐
     blockt      blockt      blockt    informiert informiert
        ▼           ▼           ▼          ▼         ▼
     EXTLOGIN     EMAIL     MEMBERSHIP   DELETE    UNLINK
        │           │
        │           ├── blockt ──────────► UNLINK   (Fall-through re-matcht per Email)
        │           ├── teilt-Daten ─────► DELETE   (Email-Freigabe ↔ partial-unique-Index)
        │           ├── teilt-Daten ─────► SOFTDELETE (Index-Prädikat = is_deleted-Flag)
        │           └── teilt-Daten ─────► MEMBERSHIP (Email ist Script-Input + Gruppen-Key)
        │
        ├── teilt-Daten ─► UNLINK     (Link & Unlink sind DERSELBE Code-Pfad)
        └── informiert ──► MEMBERSHIP (Multi-IdP flatten-and-overwrite destabilisiert Gruppen)

     UNLINK ── teilt-Daten ─► DELETE       (gleiche Mechanik; Admin-Delete hard-deleted Links schon)
     UNLINK ── kollidiert ──► MEMBERSHIP   (Link/Unlink lösen KEIN Membership-Recompute aus → stale)
     DELETE ── kollidiert ──► SOFTDELETE   (drei Pfade widersprechen sich bei PII)
     DELETE ── informiert ──► MEMBERSHIP   (manuelle Group.MemberIds werden beim Löschen nie bereinigt)
     SOFTDELETE ── informiert ► MEMBERSHIP (pending/deaktivierte User bleiben in Auto-Groups)
```

*Legende: „blockt" = muss vorher entschieden/gebaut sein · „informiert" = beeinflusst die Lösung · „teilt-Daten" = greift auf dieselben Daten/denselben Mechanismus zu · „kollidiert" = widersprüchliches Verhalten heute.*

## Fundamentale Spannungen

1. **Dokumentierte föderations-bewusste Membership vs. ausgelieferter lokal-Felder-only Hub** (`HUBPROXY`↔`MEMBERSHIP`) — `docs/concepts/auto-membership.md` nutzt `p.OrganizationalUnit` / `p.Department` / `p.externalClaims.department` als die *primäre* Art, Membership-Regeln zu schreiben, aber `Person.cs` hat nichts davon und kein `externalClaims`-Symbol existiert irgendwo in `Modgud.Authorization`. Ein Tenant-Admin, der die Doku copy-pastet, bekommt einen Transpile-Fehler. Das ist der schärfste Doc-vs-Code-Widerspruch und genau der Kern von „Hängen Hub-vs-Proxy und Membership zusammen?" — sie hängen *durch diesen Widerspruch* zusammen. Am `HUBPROXY`-Gate auflösen: wird Hub ratifiziert, die irreführenden Beispiele löschen (nicht als „geplant" behalten).

2. **Drei divergierende Lösch-Pfade widersprechen sich bei PII, Passwort-Hash, Email-Freigabe** (`DELETE`↔`SOFTDELETE`↔`EMAIL`) — Admin-`DeleteUsersCommand` (der Pfad, den die UI aufruft) setzt `IsDeleted=true`, **behält aber `Email`+`NormalizedEmail`+`PasswordHash` im Klartext** und schreibt keinen `UserDeletionState`; `EventSourcedUserStore.DeleteAsync` löscht `UserSecurityData`, behält aber Profil-PII; `GdprService` nullt alles + maskiert + archiviert. Das PII-scrubbende Erase ist hinter `gdpr:admin` gegated, einer *anderen* Permission als das alltägliche `user:write` — der Delete des normalen Operators ist also der falsche. Es gibt **heute kein wiederherstellbares Soft-Delete/Grace-Fenster** — das einzige getimte Fenster ist das GDPR-7-Tage-*Confirm*-Gate (während dessen `IsDeleted` false bleibt). Auflösen: Admin-Delete auf GDPR-Scrub-Semantik konvergieren (eine Änderung behebt den PII-Bug *und* den Email-Belegungs-Bug).

3. **Email ist ein nicht-erzwungener Identity-Key, der wie erzwungen benutzt wird** (`EMAIL`↔`EXTLOGIN`) — nirgends ein DB-Unique-Index auf Email (`MartenStoreOptionsExtensions.cs:46` ist ein gewöhnlicher non-unique Index; `Person` hat gar keinen Email-Index). Die Write-Pfade sind sich bei der Normalisierung uneinig: `CreateUserCommand`/`UpdateUserCommand`/`RecoveryCli`/`SelfRegistration` vergleichen case-sensitiv `Person.Email==raw`, während `ExternalLoginProcessor` und Identity-`FindByEmailAsync` `NormalizedEmail==UPPER` vergleichen. So können `Bob@x.com` und `bob@x.com` zu zwei Accounts werden und dann unter einem `FirstOrDefault` über eine non-unique Spalte unvorhersehbar kollidieren.

4. **Tombstone-vs-Hard-Delete-Asymmetrie** (`UNLINK`↔`DELETE`) — Self-Service-Unlink soft-tombstoned (`IsUnlinked=true`); Admin-User-Delete hard-deleted+ArchiveStreamt Links, um den Slot freizugeben. **Korrektur zum ursprünglichen Bug-Report:** das `(iss,sub)`-Lookup ist *kein* Blanket-Re-Link-Blocker — der Stale-Link-(fehlender-User)-Zweig hard-deleted + fällt schon durch (`ExternalLoginProcessor.cs:109-125`). Der Tombstone beißt nur im **live-User-aber-IsUnlinked**-Fall (`:126-132`), der `"Idp.Unlinked"` zurückgibt und verlangt, erneut über Profil → Sicherheit zu gehen. Das verkleinert den Variante-C-Fix-Scope erheblich. Es wird kein `AuthLog` für Link oder Unlink geschrieben; kein Admin-Force-Unlink-Endpoint existiert.

5. **Deaktivierung und Löschung beenden keinen aktiven Zugriff** (`SOFTDELETE`↔`DELETE`↔`EXTLOGIN`) — `IsActive=false` blockt nur *neue* Passwort-Logins (`AccountEndpoints.cs:101`); keine Security-Stamp-Rotation, kein Cookie-Sign-out, keine Token-Revocation. Und **kein Lösch-Pfad** (Admin, Identity-Store oder GDPR) revoked OpenIddict-Authorizations/Tokens, die per `Subject=userId` verschlüsselt sind. Cookies, Access-Tokens, Refresh-Tokens und Consent-Grants eines deaktivierten-oder-gelöschten Users überleben bis zum natürlichen Ablauf. Live-Security-Exposition, unabhängig von allen Datenmodell-Fixes — starker **Standalone-Hotfix**-Kandidat (gilt unter beiden Positionierungen).

6. **Membership wird bei Föderations-Änderungen sowohl über- als auch unter-evaluiert** (`MEMBERSHIP`↔`EXTLOGIN`↔`UNLINK`) — die Dependency-Tracking-Optimierung ist voll kodiert, aber inert (`MembershipScriptDependencies` immer null), sodass jedes `UserUpdated` einen vollen Evaluate-all-Durchlauf auslöst; gleichzeitig lösen Link/Unlink-Events **gar kein Recompute** aus (verifiziert: kein Handler in `AutoMembershipSyncHandlers`). Ein Script, das `p.ExternalIdentities` liest, wird bei jeder Profiländerung über-evaluiert, aber beim tatsächlichen Link/Unlink nie re-evaluiert.

## Antworten auf die zwei expliziten Fragen des Users

**„Mehrere External-Logins pro User → wie passt das zum Membership-Script?"** Das Script sieht eine *einzige flache* `Person` mit exakt vier IdP-schreibbaren Feldern. Es gibt **keinen Claim-Set-Merge** — das `UserUpdateScript` von Provider B *überschreibt* die Werte von Provider A bei jedem Login (`ExternalLoginProcessor.cs:257-329`). Bei einem User mit zwei verlinkten IdPs, die ein membership-relevantes Feld unterschiedlich mappen, können abwechselnde Logins den User **bei jedem Login in eine Auto-Group rein- und wieder rauskippen** (aktiv oszillierende Membership, nicht bloß stale). Und Link/Unlink lösen kein Recompute aus, sodass `p.ExternalIdentities`-basierte Scripts veralten, bis ein unabhängiges Profil-Event feuert.

**„Soft-Delete / Grace-Period?"** Existiert heute **nicht** als wiederherstellbares Aufbewahrungsfenster. `UserDeletionState` trackt `IsDeletionPending`/`IsDataMasked` nur für das GDPR-7-Tage-*Confirm*-Gate. Der Link-Level-`IsUnlinked`-Tombstone und der User-Level-Lösch-Lifecycle teilen **keine State-Machine** — ein künftiges „Restore" müsste Links un-tombstonen, die es nie erfasst hat. `IsActive=false`-Deaktivierung schließt einen User **nicht** aus Auto-Groups aus (Eval filtert `!IsDeleted`, nicht `!IsActive`), sodass ein deaktivierter User mit gültigem Token volle gruppen-abgeleitete Autorisierung behält.

## Neu entdeckte Kopplungen (von beiden Durchläufen verpasst, hand-verifiziert)

- **[major — Compliance] GDPR-Erase lässt verwaiste `ExternalIdentityLink`-Rows mit unmaskierter PII zurück.** `GdprService.PerformPermanentEraseAsync` maskiert nur den User-Event-Stream (7 registrierte Event-Typen) und löscht nur `UserSession`+`UserSecurityData`. Es **fasst `ExternalIdentityLink` nie an**, der `Email`, `DisplayName` und `LastRawClaims` trägt (den vollen rohen IdP-Claim-Payload, im eigenen Docstring als PII-lastig markiert). Der Pfad, dessen *einziger Zweck* PII-Löschung ist, lässt einen rohen External-Claims-Blob in der Link-Tabelle liegen. *(Hand-verifiziert: `GdprService.cs` hat null `ExternalIdentityLink`-Referenzen; nur `DeleteUsersCommand` hard-deleted Links.)*
- **[GDPR-Mask-vs-Rematch] Ein GDPR-gelöschter User wird beim nächsten SSO-Login still wiederbelebt.** Zurückkehrender External-Login → Link-Lookup findet den unmaskierten Link → `FindByIdAsync(link.UserId)` gibt null zurück (maskierter User hat `IsDeleted=true`, das `EventSourcedUserStore.FindByIdAsync` filtert) → Stale-Link-Zweig hard-deleted den Link und JIT-erstellt einen **brandneuen** User aus demselben `(iss,sub)`. GDPR-Erase ist gegen eine zurückkehrende IdP-Session nicht durabel, und der unmaskierte-PII-Link bleibt bis zu diesem nächsten Login bestehen. Akzeptabel *falls beabsichtigt*, aber undokumentiert.
- **[Ops] `StoredPasskeyCredential` ist nicht registriert und wird nie bereinigt.** Nicht in `MartenStoreOptionsExtensions` registriert (kein Unique-Index auf `CredentialId`); kein Lösch-Pfad (Admin, Identity-Store, GDPR) entfernt Passkey-Docs — also überleben Passkey-Public-Keys + User-Handles eines gelöschten Users als Waisen. Verschärft sich mit dem Last-Auth-Method-Guard, der Passkeys nicht mitzählt (`ProfileLinkEndpoints.cs:113-114`, approximiert über `HasPasswordAsync`).

## Empfohlene Sequenz

- **Phase 0 — Positionierung ratifizieren** (nur entscheiden): die „Hub-only, cycle-scoped"-Bekräftigung schreiben; als erste konkrete Handlung die `externalClaims`/`OrganizationalUnit`/`Department`-Beispiele aus `auto-membership.md` löschen und das tote Groups-Flattening in `ModgudClaimsTransformation.cs` quarantänisieren (Client parst ein Groups-Array, das der Server nie emittiert). Nimmt dem User die größte Angst, indem es sie festpinnt. *Gated durch: nichts.* — **⚠️ Hinweis: Die Federation-Sektion unten überschreibt das „Beispiele löschen" — sie sind die Spec eines gewollten Features.**
- **Phase 1 — Email als echte Invariante** (`EMAIL`): alle fünf Write-Pfade auf `NormalizedEmail==UPPER` vereinheitlichen; eine Per-Realm-Dedup-Migration laufen lassen (zwingend vor dem Indexieren); partial-unique-Index `WHERE is_deleted=false` (+ `NOT NULL`) hinzufügen, entscheiden welche Tabelle(n). *Gated durch: `HUBPROXY`.*
- **Phase 2 — Lösch-Pfade konvergieren + Access-Survival-Lücke schließen** (`DELETE`,`SOFTDELETE`): Admin-Delete auf GDPR-Scrub-Semantik leiten; `EventSourcedUserStore.DeleteAsync` stilllegen/umleiten; OpenIddict-Authorizations/Tokens auf jedem Lösch-Pfad cascade-revoken; **GDPR muss `ExternalIdentityLink` ebenfalls hard-deleten/archivieren + `Email`/`LastRawClaims` clearen**; manuelle `Group.MemberIds` bereinigen; entscheiden, ob ein wiederherstellbares Grace/Restore ein Produktziel ist. *Gated durch: `EMAIL`, `HUBPROXY`.*
- **Phase 3 — Variante-C-Unlink-Re-Link** (`UNLINK`,`EXTLOGIN`): `&& !l.IsUnlinked` zum Lookup hinzufügen; den Slot per Hard-Delete+ArchiveStream beim Unlink freigeben (das Phase-2-Primitive spiegeln) + `AuthLog` für Link/Unlink hinzufügen; Admin-Force-Unlink + Tombstone-Sichtbarkeit hinzufügen; den Last-Auth-Method-Guard härten, sodass er Passkeys zählt; Multi-IdP-Präzedenz und die SAML-`SameSite=Lax`-Link-Flow-Degradation klären. *Gated durch: `EMAIL`, `DELETE`.*
- **Phase 4 — Den Membership-Contract schließen** (`MEMBERSHIP`): `AutoMembershipSyncHandlers` an Link/Unlink-Events verdrahten + einmaliges Backfill-Recompute; die inerte Dependency-Tracking-Optimierung entscheiden (verdrahten oder löschen + Docs fixen); einen Test hinzufügen, der die zwei Evaluations-Engines (In-Memory-Delegate vs Postgres-JSONB) auf null/case/collation abgleicht. *Gated durch: `HUBPROXY`, `EXTLOGIN`, `UNLINK`, `EMAIL`.*

## Entscheidungen, die der User treffen muss (in Gating-Reihenfolge)

1. **`HUBPROXY` für diesen Zyklus ratifizieren.** → *(Überholt durch die Federation-Sektion: Hub-by-default + Broker-Opt-in statt Hub-only.)*
2. **Email-Index: Zieltabelle(n), `NOT NULL`, Normalisierungs-Vereinheitlichung.** → *Empfehlung: BEIDE `ApplicationUser` und `Person` `WHERE is_deleted=false` indexieren, `NOT NULL` auf dem Menschen-Pfad, zuerst alle Vergleiche auf `NormalizedEmail==UPPER` vereinheitlichen.*
3. **Kanonische Lösch-Semantik + OAuth-Cascade-Revoke + wiederherstellbares Grace?** → *Empfehlung: sofort auf GDPR-Scrub konvergieren + OAuth cascade-revoken, außer es gibt eine konkrete Restore-/Audit-Aufbewahrungs-Anforderung. Token-Revocation ist ein Standalone-Hotfix.*
4. **Variante-C-Re-Match-Policy, wenn eine Email nach einem Delete/Recreate von einem neuen User wiederverwendet wurde.** → *Empfehlung: Re-Home über den existierenden Per-Provider-`TrustForEmailLink`-Knopf gaten; sonst einen frischen, bewussten Self-Service-Link verlangen. Als Test festhalten.*
5. **Unlink-Slot-Freigabe-Mechanik + Audit.** → *Empfehlung: Hard-Delete + ArchiveStream beim Unlink (Admin-Delete spiegeln) plus den `AuthLog` in jedem Fall hinzufügen.*
6. **Den `auto-membership.md`-Doc/Schema-Widerspruch auflösen.** → *(Überholt durch die Federation-Sektion: die Beispiele sind die Spec einer projizierten, push/pull-gespeisten externalGroups-Fläche, nicht zu löschen.)*

## Standalone-Hotfix-Kandidaten (vor dem Untangle shippbar — gelten unter beiden Positionierungen)

- OAuth-Token/Authorization + Session-Revocation beim Löschen (und idealerweise beim Deaktivieren). Live-Security-Exposition.
- GDPR-Erase muss `ExternalIdentityLink.Email` + `LastRawClaims` scrubben. Live-Compliance-Exposition.

## Federation Group-Sync: Prior Art + empfohlenes Modell (2026-05-28) {#federation-prior-art}

Diese Sektion überschreibt die „Hub-only ratifizieren / Doc-Beispiele löschen"-Empfehlung weiter oben. Sie ist das Ergebnis eines 16-Agenten-Web-Research-Workflows (7 System-Deep-Reads → Synthese → 8 adversariale Verifikationen). Verifikations-Ergebnis: 6/8 tragende Behauptungen **bestätigt**, 1 **teilkorrekt** (Keycloak-Issue #31539 schlägt vor, `IMPORT` zum Default-Broker-Sync-Mode zu machen, nicht `FORCE` — fürs Ergebnis irrelevant), 1 **widerlegt** (das Zitat „group sync does not handle deprovisioning … configure SCIM instead" stammt aus Optimizelys Doku, **nicht** von Okta; der Richtungspunkt hält dennoch über die *bestätigte* Tatsache, dass Oktas Default-JIT-Gruppen-Zuweisung add-only ist).

### ⭐ Beschlossene v1-Richtung (2026-05-28, mit User konvergiert)

v1 ist das **pure-ephemere, session-scoped** Ende des Spektrums. Kein Lease, keine persistierte externe Membership, keine gespeicherten Claim-Snapshots — das sind verschobene, **additive** Layer (keiner verbaut den Weg, weil alle auf demselben Kern aufsetzen).

**Was Hub und Federation unter einen Hut bringt:** Hub-vs-Federation ist kein *Modus des Realms*, sondern eine *Eigenschaft der Herleitung jeder einzelnen Mitgliedschaft*. Ein Realm/ein User kann gleichzeitig manuelle + lokal-Attribut- + externe-Claim-abgeleitete Memberships haben; alle laufen durch **eine** Pipeline und kommen als **ein** Satz Modgud-Rollen im Token raus. Die App sieht nur Modgud, nie dass eine Rolle aus EntraID kam — das Hub-Versprechen, mit unsichtbarer Federation dahinter. Identität bleibt strikt Hub (ein kanonischer lokaler User; externe Logins bleiben `(iss,sub)→user`-Links; Token emittiert nur Modgud-eigene Rollen, nie rohe Upstream-Claims). Federation kommt als Autorisierungs-*Input*, nicht als Identitäts-Autorität.

**Per-Login-Pipeline:**
1. Claims des aktuellen Providers (inkl. Roles/Groups) lesen, getaggt `source=provider:<slug>`, neben `source=local`. **Live only — in v1 nicht persistiert.**
2. JsEval als Transform-Stufe: Claim-Transformation + berechnete Claims (z.B. FullName aus first+last). Erweitert vom heutigen 4-Felder-Patch.
3. Der bestehende **In-Memory-Per-Principal-Membership-Evaluator** läuft über `local ∪ Claims des aktuellen Providers` → Membership in-memory berechnet. (Die Postgres-JSONB-Batch-Query *kann* ephemere Claims nicht sehen → in-memory ist das richtige & einzige Werkzeug.)
4. Ergebnis lebt in Modgud-Session + ausgestelltem Token; **nie in die persistierten `Group.MemberIds` geschrieben.**
5. Jeder (privilegierte) extern-abgeleitete Grant → AuthLog-Event.

**Die Session *ist* der Lease:** extern-abgeleitete Membership existiert nur, solange eine über diesen Provider authentifizierte Session lebt. Session/Token-TTL begrenzt die Staleness; Ablauf = fail-closed Verfall; Re-Login = neu herleiten. „Wer ist gerade extern in Gruppe G?" = aus aktiven Sessions ableitbar, keine persistierte (veraltende) Tabelle. (Kein separater Lease-Mechanismus nötig.)

**Guardrails (hier sitzt die Sicherheit, NICHT in Tabelle-vs-Script):**
- Per-Provider explizites „trusted for authorization"-Flag (spiegelt `TrustForEmailLink`). Echte Gefahr ist *nicht-vertrauenswürdiger/User-beeinflussbarer Claim → Privileg*, nicht „extern treibt Gruppe".
- Per-Gruppe explizites Opt-in „darf von externen Claims getrieben werden" (v.a. privilegierte Gruppen) — explizit + geloggt, nicht verboten (Verbieten würde das Feature töten).
- `realm:admin` empfohlen **local-only** (Federation-Fehlkonfig darf den Tenant nie aus seinem letzten lokalen Admin aussperren). `app:admin` und darunter extern-treibbar.
- Die zwei Membership-Engines (Postgres-JSONB-Batch vs. In-Memory-Per-Principal) MÜSSEN bei null/case/collation übereinstimmen — kritischer Test, sonst klassifiziert derselbe User je nach Pfad anders.

**Ehrliche Naht (inhärent, kein Bug):** föderierte Memberships sind in der Admin-UI **nicht enumerierbar** (nur lokale). Die UI muss eine Gruppe ehrlich zeigen als „N bekannte lokale Mitglieder + externe werden beim Login bestimmt (aus Provider X, Y)". Transparenz via AuthLog der Grants-beim-Login.

**Auditierbarkeits-Trade-off:** ein Membership-*Script* ist mächtiger, aber weniger deklarativ auditierbar als eine `extgroup→group`-Tabelle; das Grant-beim-Login-AuthLog fängt das zur Laufzeit auf. Eine deklarative Mapping-Tabelle als *Zucker* für Simpel-Fälle ist ein späterer additiver Zusatz.

**Verschoben (additiv, verbaut nichts):** durable-mit-Lease-Enumeration; gespeicherte Per-Source-Claim-Snapshots für Was-wäre-wenn/Forensik; deklarative `extgroup→group`-Mapping-Tabelle als Zucker. Begründung dass es nicht verbaut: alle drei hängen sich an die gemeinsame Source-Attribution + Compute-at-Login-Pipeline, die v1 schon baut.

### Der entscheidende Befund

**Jeder untersuchte IdP hat das Stale-Admin-Loch in irgendeiner Default-Konfiguration, und keiner ist fail-closed.** Alle gehen per Default auf *persist-and-reconcile* (eine durable Mitgliedschaft überlebt ein verpasstes Deprovision-Event). Die Anforderung des Users — *decay-unless-reconfirmed* (verfallen, sofern nicht re-bestätigt) — ist **strenger als Keycloak, Okta, Entra, Auth0, Zitadel und Ping**. Das bestätigt das Misstrauen des Users gegen SCIM-als-Sicherheitsnetz: SCIM ist der einzige login-unabhängige Kanal, den die Anbieter liefern, aber er konvergiert in Minuten-bis-Stunden, Gruppen-Provisioning ist in RFC 7644 *optional*, Implementierungen sind inkonsistent, und **verpasste Events werden nicht nachgesynct**.

### Wie die Prior Art mit dem Stale-Admin-Szenario umgeht

| System | Gelöst? | Mechanik |
|---|---|---|
| **Keycloak** | ❌ | Reconcile ist rein login-getrieben. Default-Sync-Mode `LEGACY` reconcilet gar nichts; `FORCE` läuft Gruppen-/Rollen-Mapper (`joinGroup`/`leaveGroup`) nur beim nächsten Login *über diese IdP* erneut. Issue #36578: Re-Pointing eines Mappers lässt den User in *beiden* Gruppen (add-biased, kein idempotentes Set-Reconcile). Kein Background-Reconcile für brokered IdPs (periodischer Sync ist LDAP/Kerberos-only). Inbound-SCIM experimentell in 26.6, off by default, nicht in die Mapper-Pipeline verdrahtet. |
| **Okta** | ⚠️ teilweise | Default-JIT-Gruppen-Zuweisung ist add-only („subsequent logins do not remove them"). Per-IdP „Full Sync of Groups" entfernt Gruppen, die nicht in der inbound Assertion sind — aber nur beim Sign-in über diese IdP. Group Rules (OEL, bidirektional) evaluieren nur das *lokale* Universal-Directory-Profil; können keine rohen Upstream-Claims lesen. Strukturelles Guardrail zum Kopieren: Rules/JIT dürfen keine Admin-Groups befüllen, und eine Rule-Ziel-Gruppe kann kein Admin erlangen. |
| **Microsoft Entra** | ✅ strukturell | Weigert sich, Upstream-Gruppen-Claims zu durable lokaler Authz zu machen; Föderation erzeugt ein lokales Objekt, Authz wird pro Token-Ausgabe neu berechnet. Upstream-getriebenes Privileg wird via Cross-Tenant-Sync/SCIM (~20-40 min Push) provisioniert. Dynamic-Membership-Groups evaluieren nur *lokale* Attribute. **Role-assignable Groups verbieten Dynamic Membership.** Caveats: Gruppen-Änderung ist *kein* Near-Real-Time-CAE-Event („bis zu einem Tag"); CAE deckt keine B2B-Gäste ab. |
| **Auth0** | ❌ | Naives Post-Login-Action-`assignRoles` ist die Falle. Mitigation via Inbound-SCIM-Deactivate (killt Sessions + Refresh-Tokens) + ephemeres `setCustomClaim` statt durable Rollen + kurze Token-TTL. Keine native Event-getriebene Entfernung. |
| **Zitadel** | ❌ | Kann externe Gruppen-/Rollen-Claims nicht nativ auf lokale Rollen mappen (#8093). Der einzige Login-Refresh-Hook (`PostAuthentication`) kann nicht granten. Mahnbeispiel, keine Vorlage. |
| **Ping** | ✅ abseits puren JIT | PingFederate-Inbound-SCIM oder PingOne-Scheduled-Inbound-Provisioning (Poll ~15/30 min) propagieren Upstream-Entfernung ohne Re-Login. Pures PingOne-JIT-External-Groups kann eine manuell entfernte Gruppe *wieder hinzufügen*; JIT in PingFederate ist create-only (kein Gruppen-Lifecycle). |
| **SCIM 2.0** | ✅ der Kanal | RFC 7642 §1 trennt Provisioning von JIT; out-of-band `PATCH`-Remove auf `Group.members` / `active=false` konvergiert unabhängig vom Login-Pfad. Aber Minuten-bis-Stunden, Gruppen-Provisioning optional, Impls inkonsistent, **verpasste Events nicht nachgesynct** (genau der Einwand des Users). |

### Das herrschende Industrie-Muster (4 Säulen) + unsere 5.

Die Quellen konvergieren (Curity, OWASP, RFC 9700, NIST SP 800-63B-4, Anbieter-Docs):
1. **Niemals** einen Login-Zeit-Gruppen-/Rollen-Claim als undifferenzierte durable Kante persistieren (das universell dokumentierte Stale-Admin-Anti-Pattern).
2. **Jede** durable Mitgliedschaft ihrer Quelle **attribuieren** und **per Quelle als SET** abgleichen (add *und* remove). Naive Union-ohne-Attribution ist genau der Grund, warum ein degradierter EntraID-Admin, der sich später per Passwort einloggt, den Grant behält.
3. Durable externe Authz an einen **out-of-band-Reconciliation-Kanal hängen, der vom Login-Pfad unabhängig ist** (SCIM-Push oder Scheduled Pull).
4. **Das Restfenster begrenzen**: kurze Access-Token-TTL + Re-Derive beim Refresh + ein Revocation-Signal (RFC 7009 Token-Revocation, OpenID CAEP `token-claims-change`/`session-revoked` über Shared Signals, OIDC Back-Channel Logout).
5. **Modguds Ergänzung (strenger als alle Prior Art):** ein **Lease/TTL pro externem Grant, der zu „abwesend" verfällt, sofern nicht aktiv re-bestätigt** (durch Login-FORCE, SCIM-Push oder Scheduled Pull). Ein still verpasstes Deprovision heilt sich durch Ablauf, statt zu persistieren → **fail-closed**.

### Wo JsEval-Auto-Membership hineinpasst

Als Fähigkeit nicht neu — **Okta Group Rules** und **Entra Dynamic-Membership-Groups** sind direkte Analoga. Die entscheidende Übereinstimmung: *alle drei evaluieren nur über das lokale/kanonische Profil und können keine rohen Upstream-Claims lesen* — exakt Modgud heute. Das ist die branchen-gesegnete **Zwei-Stufen-Pipeline** (Upstream-Daten zuerst auf den lokalen Principal normalisieren, *dann* Regeln über die lokale Projektion laufen lassen); **beibehalten.** Modguds echte Unterscheidungsmerkmale sind schmal: ein echter TS→LINQ-Transpiler (ausdrucksstärker als Entras beschränkte DSL, kleinere Angriffsfläche als Auth0s volle Node.js-Actions) und eine Batch-Set-Query statt eines Per-Token-Claim-Shapers. Gefahr (bei allen dreien identisch): berechnete Membership ist nur so frisch wie ihre Inputs — wenn eine projizierte `externalGroups`/`externalClaims`-Fläche je **login-snapshot-gespeist** wird, baut das Script die Stale-Admin-Falle nach. Sie ist nur sicher, wenn out-of-band push/pull-gespeist **und** lease-gestempelt.

### Empfohlenes Modell

- **Manuelle + lokale-Attribut-JsEval-Membership: unverändert** (durable, autoritativ, kein Verfall — Modgud besitzt sie).
- **Neue herkunfts-attribuierte External-Membership-Klasse**: jeder externe Grant trägt `(groupId, principalId, source = provider:<slug>, grantedAt, leaseExpiresAt, lastReconfirmedAt)`. Heute ist `Group.MemberIds` eine flache `List<Guid>` ohne Herkunft — diese Flachheit *ist* die strukturelle Ursache der Falle.
- **Effektive Member = Union { manuell } + { lokal-JsEval } + { pro-Provider extern, WHERE leaseExpiresAt > now }.** Jeder Provider besitzt und SETtet (add+remove) nur sein eigenes Subset; manuelle/lokale Subsets werden vom externen Reconcile nie angefasst.
- **Refresh-Trigger**: (a) Login-FORCE → das Subset von Provider X aus der präsentierten Assertion SETzen (idempotent, Abwesendes entfernen — der #36578-Fix), Lease erneuern; (b) out-of-band Inbound-SCIM oder Scheduled Pull (Graph/LDAP) pro Provider → dasselbe SET, login-unabhängig; (c) **Lease-Expiry-Sweep (Quartz, im Repo) = die fail-closed Autorität letzter Instanz**; (d) lokale-Attribut-Änderung → nur lokales JsEval re-evaluieren (das aktuell inerte `MembershipScriptDependencies` verdrahten); (e) **Link/Unlink → Recompute** (löst aktuell keins aus — bestätigte Lücke).
- **Token-Semantik**: nur Modgud-eigene Permissions/Rollen über die **Union aller aktuellen (nicht-abgelaufenen) Mitgliedschaften** emittieren (strikter Hub an der Grenze; niemals rohe Upstream-Claims). Kurze Access-Token-TTL + Re-Derive beim Refresh; expliziter Revoke fürs Restfenster.
- **Privileg-Guardrail** (Entra role-assignable Groups + Okta no-admin-rule spiegeln): föderierte/JsEval-Auto-Membership darf niemals `realm:admin` / `app:admin` verleihen; die kommen nur aus manuell oder einem reconcilenden Kanal.

### Forks aufgelöst

1. **Durable vs. ephemer** → *konfigurierbar pro Provider*, Default **durable-mit-Lease** (nicht ewig, nicht rein-ephemer). Pure-ephemer (bei jedem Login neu hergeleitet, nie persistiert) als Option für Low-Stakes-Provider. Niemals durable-ohne-Reconcile anbieten. Keine von beiden darf Admin-Tiers verleihen.
2. **Upstream-Änderung erfahren ohne einen Login über diese IdP** → geschichtet: optional Inbound-SCIM, optional Scheduled Pull, **immer-an Lease-Expiry als fail-closed Notnagel** (die harte Anforderung), plus kurze Token-TTL + RFC 7009/CAEP-Revoke.
3. **Mapping-Mechanismus** → primär = explizite, auditierbare Per-Provider-`extgroup → Modgud-group`-**Mapping-Tabelle** (Keycloak/Okta/Entra/Ping-Konsens; Entfernen/Reconcile ist handhabbar und reviewbar). Lokale-Attribut-JsEval wie heute behalten. Projizierte `externalGroups`/`externalClaims` für JsEval nur als sekundäre Advanced-Fläche freigeben, **nur falls push/pull-gespeist + lease-gestempelt, niemals login-gespeist**. (Genau das sollten die `auto-membership.md`-`p.externalClaims.*`-Beispiele werden, falls gebaut.)
4. **Token-Union vs. Login-Pfad** → **Union aller aktuell nicht-abgelaufenen** Mitgliedschaften, emittiert nur als Modgud-Rollen. Pfad-only-Tokens sind genau das, was Privileg beim Pfadwechsel nicht-widerrufbar macht; Union-of-current ist sicher, *weil* der durable Store selbst lease-reconciled ist.

### Offene Risiken (ins Design mitnehmen)

- **Fail-closed-Lease vs. Usability**: ein legitimer User, der sich nicht über diese IdP re-loggt und dessen Pull/SCIM down ist, verliert beim Lease-Ablauf den Zugriff. TTL pro Provider/Tier tunen (NIST 800-63B-4-Obergrenzen: ~24h normal, ~12h high-priv) und **Admins alarmieren, wenn Grants wegen eines kaputten Kanals verfallen**, sonst wirkt es wie Flakiness.
- **Heute existiert weder ein SCIM-Server noch ein LDAP-Client** — der kurzfristige Boden ist Lease + Scheduled Pull; Inbound-SCIM ist Net-New-Surface.
- **Idempotentes SET-Reconcile ist genau das, was Keycloak verbockt hat** (#36578); muss `provider:X`-Grants entfernen, die in der letzten Assertion/im letzten Pull fehlen, auch bei Mapping-Tabellen-Re-Point.
- Der Lease-Expiry-Sweep ist ein Per-Realm-Background-Job über N physische Postgres-DBs (Master-Table-Tenancy) — Quartz-Fan-out + System-Tenant-Fallback braucht Design.
- Der neue Grant-Store ist eine weitere PII-/Lifecycle-Fläche, die Lösch-/GDPR-Pfade mit-cascaden müssen (vgl. die `ExternalIdentityLink`-PII-Lücke oben).
- Token-Re-Derive hilft nur, wenn OpenIddict-7-Refresh aus dem lease-reconcilten Store neu berechnet, statt eingefrorene Claims neu auszugeben; das Rest-Access-Token-Fenster ist ohne RFC 7009/CAEP nicht-widerrufbar (noch nicht implementiert).

### Wichtige Quellen

Keycloak: Sync-Mode + Mapper-Javadoc, Issues #31539 (Default→IMPORT-Vorschlag), #36578 (Beide-Gruppen-Bug), SCIM-experimentell-Blog (2026-04-10). Okta: JIT add-only + „Full Sync of Groups" (Org2Org-Docs), Group Rules + OEL (können keine Upstream-Claims lesen), Group-Rule-Admin-Restriktionen. Entra: Dynamic Groups über lokale Attribute, Gruppen-Änderung kein CAE-Event („bis zu einem Tag"), role-assignable Groups verbieten Dynamic Membership, CAE schließt B2B-Gäste aus, Cross-Tenant-Sync ~20-40 min Push. Auth0: Inbound-SCIM hat kein `/groups`, Account-Link verwirft Secondary-Metadata, Continuous Session Protection ≠ CAE. Zitadel: #8093. Ping: PingFederate-SCIM vs. create-only-JIT, PingOne-Poll-Kadenz. Standards: RFC 7642/7643/7644 (SCIM), RFC 7009 (Revocation), RFC 9700 (OAuth-Security-BCP), OpenID CAEP/Shared Signals, OIDC Back-Channel Logout, NIST SP 800-63B-4 Reauthentifizierungs-Obergrenzen.

## Herkunft (Provenance)

Untangle-Map: Workflow `wf_68397a33-f6a` (10 Agenten, ~993k Tokens). Federation-Prior-Art: Workflow `wf_a245f4b2-ab2` (16 Agenten, ~914k Tokens, web-recherchiert + adversarial verifiziert). Rohe strukturierte Outputs liegen unter `.local/wf-*.json` und `.local/wf2-*.json` für diese Session.
