# Future Features

Capabilities we know we'll need to build but haven't prioritised.
Each page captures the design space — options, trade-offs, risks —
so that when a customer ask makes one of them urgent, we don't have
to re-derive the analysis from scratch.

## Pages

### [White-label customization](./white-label-customization)

Per-realm theming: logo, colors, brand copy, optional custom CSS.
**Standard ask** from every paying customer once they're past the
"does it work?" phase. Three escalation tiers (theme tokens →
custom copy → custom CSS), with a phased rollout plan that ships
the 80%-coverage version first.

**Status:** Design captured 2026-05-07. Not started.

### [Login alerts + manual IP blacklist](./login-alerts-ip-blacklist)

Brute-force visibility without NAT-aussperr-Risk: detect anomalous
failed-login volume, alert admin, let the human decide whether to
blacklist. The NAT-safe alternative to a naive IP rate-limit
(which we explicitly rejected — see
`feedback_no_ip_rate_limit_password_login.md` in claude memory).

**Status:** Idea captured 2026-05-07. Not started.

### [Permission-Modell (finaler Stand)](./permission-modell)

Konsolidierte deutsche Zusammenfassung aller Designgespräche zum
zukünftigen Permission-Modell: App-Catalog mit `<resource>:<action>`
Format, RS-Subset, 2-Tier-Bypass-Modell, UserInfo nur Identity,
Distribution-API als einziger Authz-Kanal, Lib-Aufgaben + RS-Code-
Konvention. *Diese Seite ist die autoritative Kurzfassung; die
beiden folgenden Notes sind das Detailmaterial dahinter.*

**Status:** Designkonsolidierung 2026-05-08. Nicht implementiert.

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

### [Implicit-Scope-Per-API + Discovery-Privacy](./scope-api-coupling-and-discovery-privacy)

Erste externe Integration (EventTree) zeigte zwei UX-Schmerzen:
Admin muss API + Scope doppelt anlegen für die 1:1-Standardkopplung,
und `.well-known/openid-configuration` leakt alle App-Scope-Namen
und damit die Realm-Topologie. Designkonsens: API-aware
Scope-Resolver (impliziter Scope = API-Name, kein separater
DB-Row), Discovery filtert auf OIDC-Standards, opt-in `IsPublic`
per Scope. Standards-Trennung bleibt — nur Verwaltungs-UX +
Sichtbarkeit ändern sich.

**Status:** Designkonsens 2026-05-11. Nicht implementiert.

### [Application as permission catalog; Resource Server gets a subset](./app-resources-as-permissions)

⚠️ **Teilweise superseded durch das oben verlinkte
[Permission-Modell](./permission-modell).** Die App-as-Catalog-
Grundidee + RS-prefix-free-Rationale + ID-anchored-Entities-
Begründung gelten weiter. Was revidiert wurde: Token-Claim-
Emission verworfen (Distribution-API stattdessen), Bypass-Tiers
auf 2 reduziert, Slug-tagged-Format auf bare reduziert, Roles
aus UserInfo entfernt. Diese Note bleibt als Designexploration —
Detail-Banner oben in der Note.

**Status:** Note 2026-05-07, teilweise revidiert 2026-05-08.

### [DCR for MCP clients](./dcr-for-mcp-clients)

Dynamic Client Registration (RFC 7591) so AI agents (Claude Code,
Cursor, Continue, …) can self-register against the IdP and attach
to cocoar-internal MCP servers without per-agent admin onboarding.
First trigger is `cocoar-policy` wanting `auth.cocoar.dev` as its
IdP. Distinct from user self-registration (which shares the word
"register" but is a totally different concept).

**Status:** Parked 2026-05-07. Not before Resource Indicators
(RFC 8707) ships.

---

## Adding to this section

1. Create `dev-notes/future-features/<feature-slug>.md`
2. Open with **Status** + **Why** lines (see existing pages for
   format)
3. Capture the design space, not the final design — options with
   pros/cons, risks, what would block, what could phase
4. Register in `.vitepress/config.dev-notes.ts` so it shows up in
   the local sidebar
5. Link from this page

When the feature ships, **promote** the page: move it into the
appropriate public section (`/concepts/`, `/guide/`, `/admin/`,
or `/reference/`), update the sidebar registrations, delete the
dev-notes entry. Don't leave stale "future" docs around once the
future has arrived.
