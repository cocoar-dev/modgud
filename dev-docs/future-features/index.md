# Future Features

Capabilities we know we'll need to build but haven't prioritised.
Each page captures the design space — options, trade-offs, risks —
so that when a customer ask makes one of them urgent, we don't have
to re-derive the analysis from scratch.

## Production-Readiness-Roadmap

⭐ **Einstiegspunkt für „wie weit sind wir?":**
[Production-Readiness-Audit 2026-05-13](./production-readiness-audit-2026-05-13)
— ehrliche Audit-Zusammenfassung mit Vergleich gegen Keycloak / Auth0
/ Zitadel, Verdict, und Backlog-Tabelle mit allen offenen Punkten und
Severity. Detail-Pages unten.

### Audit-Followups (in Severity-Reihenfolge)

- Observability — OpenTelemetry / Metrics / Tracing — ✅ shipped (see
  [Observability](/operate/observability)).
- [HA / Multi-Instance Readiness](./ha-multi-instance) — DataProtection,
  Distributed Rate-Limiter, IDistributedCache, SignalR-Backplane.
  (Phase 2a Deployment-Hygiene ✅ shipped 2026-05-13.)
- [Realm Backup / Restore / DR](./realm-backup-restore) — CLI für
  N-Tenant-DB-Backup/Restore + WAL-Pattern + Realm-Migration.
- [Enterprise SSO — SAML + LDAP](./enterprise-sso-saml-ldap) —
  customer-getrieben, Designspace captured.
- [SAML federation — implementation plan](./saml-federation) —
  konkreter Implementation-Plan für den SP-Use-Case (Modgud konsumiert
  Customer-IdP). Lib-Wahl: ITfoxtec.Identity.Saml2. Status:
  Plan-Page captured 2026-05-27, nicht gestartet.

## Andere Future-Features

### [White-label customization](./white-label-customization)

Per-realm theming: logo, colors, brand copy, optional custom CSS.
**Standard ask** from every paying customer once they're past the
"does it work?" phase. Three escalation tiers (theme tokens →
custom copy → custom CSS), with a phased rollout plan that ships
the 80%-coverage version first.

**Status:** Design captured 2026-05-07. Not started.

### [Service Account credentials — link to OAuth Clients](./service-account-credentials)

How machine identities (ServiceAccount principal) authenticate against the
IdP. Standard answer: bind a `client_credentials`-only OAuth Client 1:N
to the SA; token-endpoint resolves `sub = SA.Id` so audit logs and the
Group → Role → Permission chain work the same for machines and humans.
Includes the rule that user-flow clients (`authorization_code`) and M2M
clients are strictly separated — one OAuth Client = one auth mode.

**Status:** Shipped 2026-05-24. ServiceAccount CRUD UI (Phase 2B)
+ credentials linkage + strict grant separation + cascade-delete
+ migration CLI (Phase 2C) all landed; live doc is
[`docs/admin/service-accounts.md`](../../docs/admin/service-accounts.md).

### [Login alerts + manual IP blacklist](./login-alerts-ip-blacklist)

Brute-force visibility without NAT-aussperr-Risk: detect anomalous
failed-login volume, alert admin, let the human decide whether to
blacklist. The NAT-safe alternative to a naive IP rate-limit
(which we explicitly rejected — see
`feedback_no_ip_rate_limit_password_login.md` in claude memory).

**Status:** Idea captured 2026-05-07. Not started.

### [Permission-Modell (finaler Stand)](./permission-modell)

Konsolidierte deutsche Zusammenfassung aller Designgespräche zum
Permission-Modell: App-Catalog mit `<resource>:<action>` Format,
RS-Subset, 2-Tier-Bypass-Modell, UserInfo per-Audience-nested
Emission (Roles + Permissions + Groups bypass-pre-expanded),
Lib-Aufgaben + RS-Code-Konvention. *Diese Seite ist die autoritative
Kurzfassung; die beiden folgenden Notes sind das Detailmaterial
dahinter.*

**Status:** Implementiert (Stand 2026-05-09).

### [Permission-Modell — Adversarial Review](./permission-modell-adversarial-review)

⚠️ **Pflichtlektüre vor Implementation des Permission-Modells.**
Vier parallele Reviewer haben das Design gegen Single-Aud-, Multi-
Aud-, Lib-less- und Edge-Case-Szenarien geprüft und mehrere
kritische Sicherheits- und Spec-Lücken gefunden — drei davon
unabhängig von 2-3 Reviewern bestätigt. Findings, Fixes und
Empfehlungen in dieser Note. Die Hauptnote
[permission-modell.md](./permission-modell) ist dadurch noch nicht
implementations-reif.

**Status:** Review 2026-05-08. 3 kritische Findings + 12 wichtige
Findings + 8 vorgeschlagene empirische Tests.

### [UserInfo Hybrid-Emission für Single-Aud-Fall](./userinfo-hybrid-flat-emission)

Optionale additive Erweiterung des Permission-Modells: bei
Single-Audience-Tokens UserInfo zusätzlich flach emittieren,
damit RSes ohne Cocoar-Helper-Lib Roles via Standard-ASP.NET-
Konfiguration konsumieren können. Geparkt — kein heute
existierender Konsument, aber jederzeit additiv nachrüstbar.

**Status:** Geparkt 2026-05-08. Nicht blockierend.

### [CI iteration hygiene — make workflow development cheap](./ci-iteration-hygiene)

Five concrete items to make CI cheap to iterate on: `workflow_dispatch`
+ `dry_run` on `cd-release.yml` (so the release pipeline is testable
without cutting releases), `paths-ignore` on `codeql.yml` (skip
docs-only PRs), `act` local-runner setup doc, composite actions for
repeated setup blocks, and a `ci/**` branch-trigger escape hatch.
Stufe C (mandatory PRs on `develop`, no admin bypass) ships **only
after** this wave — the gate was "trivial changes must remain cheap".

**Status:** ✅ Shipped 2026-05-26 (`fd70785`). Stufe C activation
unblocked. Live how-to in
[docs/contribute/local-ci.md](../../docs/contribute/local-ci).

### [NodaTime migration — store intent, not math](./nodatime-migration)

Today every timestamp in Modgud is a `DateTimeOffset` UTC instant —
correct for "happens N minutes from now" semantics (token expiry,
magic links, audit logs) but wrong for any **future-scheduled
event with admin-intended local time** (e.g. "deactivate user on
2026-06-27 at 18:00"). 18:00 needs an IANA zone or the intent is
lost the moment EU drops DST. Plan covers the migration to
NodaTime (`Instant` + `LocalDateTime + DateTimeZone`),
OpenIddict-boundary strategy, Marten/Postgres mapping, Quartz
TZ-coupling, and a `(localDateTime, zoneId)` API contract paired
with the Temporal-based date-time component suite already shipping
in `@cocoar/vue-ui` (`CoarZonedDateTimePicker` &c).

**Status:** Plan captured 2026-05-26. Scheduled to run as the
first post-public-flip refactor wave. Pre-1.0 is the cheapest
moment — no user data to migrate, no contributors to retrain.

### [Per-App Login-Customization (Routing + Form-Builder)](./per-app-login-customization)

Modgud zentralisiert Login — heute ein Realm = eine Login-Seite,
alle Apps teilen sich denselben UI. Wenn derselbe Kunde mehrere
Produkte fährt (alpha-blog, beta-shop, event-tree, …) will Marketing
dass jedes wie es selbst aussieht. Design: App-Context-aware Routing
(Subdomain ODER Subpath ODER implicit via `client_id`, alle konvergieren
auf denselben Slot) plus Form-Builder mit vorgefertigten Bausteinen
(UsernameField, PasswordField, MagicLinkButton, PasskeyButton, …) statt
free-HTML — Security + A11y eingebaut. Builds auf
[white-label-customization](./white-label-customization) (per-Realm)
auf, ist die feinere App-Schicht darunter.

**Status:** Designkonsens 2026-05-12. Nicht implementiert.

### [Application as permission catalog; Resource Server gets a subset](./app-resources-as-permissions)

⚠️ **Teilweise superseded durch das oben verlinkte
[Permission-Modell](./permission-modell).** Die App-as-Catalog-
Grundidee + RS-prefix-free-Rationale + ID-anchored-Entities-
Begründung gelten weiter. Was revidiert wurde: Token-Claim-
Emission verworfen (UserInfo-Emission stattdessen), Bypass-Tiers
auf 2 reduziert, Slug-tagged-Format auf bare reduziert. Diese Note
bleibt als Designexploration — Detail-Banner oben in der Note.

**Status:** Note 2026-05-07, teilweise revidiert 2026-05-08.

### DCR for MCP clients — ✅ shipped

Dynamic Client Registration (RFC 7591) for AI agents to self-register
against the IdP. Shipped 2026-05-12, see [Dynamic Client
Registration](/admin/dynamic-client-registration) for the live admin
page.

<!-- Below is historical-design-only — kept for context but no longer a future feature.

**Status (pre-ship):** v1 design locked 2026-05-12, **ready to implement
(7-8 days)** — consent-UI prereq shipped same day (commit
`9090007`), nothing blocking. MCP-flavoured scope: public PKCE
only, triple opt-in (realm master toggle +
per-`OAuthApi.AllowDynamicRegistration` +
per-`OAuthScope.AllowDynamicRegistrationClients`),
HTTPS-resource-indicator mandatory, `client_name`-spoofing rules
(NFKC + Latin-1 whitelist + realm-configured reserved-names
blocklist), `[unverified]` marker + warning text on consent,
tighter token TTLs for DCR clients, refresh-rotation globally on,
5 dedicated audit-event types, 90-day GC TTL.

-->

---

## Adding to this section

1. Create `dev-docs/future-features/<feature-slug>.md`
2. Open with **Status** + **Why** lines (see existing pages for
   format)
3. Capture the design space, not the final design — options with
   pros/cons, risks, what would block, what could phase
4. Register in `.vitepress/config.ts` so it shows up in
   the local sidebar
5. Link from this page

When the feature ships, **promote** the page: move it into the
appropriate public section (`/concepts/`, `/integrate/`, `/operate/`,
`/admin/`, `/plattform/`, or `/reference/`), update the sidebar
registrations, delete the dev-docs entry. Don't leave stale "future"
docs around once the future has arrived.
