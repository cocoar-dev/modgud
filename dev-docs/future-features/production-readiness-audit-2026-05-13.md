# Production-Readiness-Audit (2026-05-13)

> **Status:** Audit-Snapshot. Roadmap-Punkte werden in separaten Pages
> ausgearbeitet, wenn sie ranschneiden.
> **Why:** Ehrliche Einschätzung wo Modgud gegenüber etablierten
> IdPs (Keycloak / Auth0 / Zitadel) steht — was solide ist, was riskant,
> was schlicht fehlt. Geboren aus einem agent-gestützten Audit gegen
> Codebase + Memory + Hardening-Track-Record.

## TL;DR

Modgud ist **kein Hobbyprojekt** — OpenIddict-basiert,
33 Audit-Findings systematisch geclosed, 4 SAST-Schichten, ~928 Tests
grün. Aber es ist **scharf zugeschnitten auf einen Owner-Operator-
Use-Case**, nicht auf „Drop-in-Replacement für Keycloak".

**Ready für:**

- Eigener SaaS-Stack (Cocoar-Apps + handvoll Tenant-Realms)
- Single-Instance hinter Reverse-Proxy
- Owner als Operator
- <50 Realms, <100k User
- OIDC-only Clients

**Nicht ready für:**

- Multi-Instance / HA (DataProtection + Caches + Rate-Limiter alle
  In-Memory)
- Enterprise-SSO (SAML 2.0 SP ✅ seit 2026-05-28 / PR #17; LDAP/AD weiter offen)
- Fremde Kunden mit Compliance-Audit (kein SOC2/ISO27001)
- Mehr als ~10 aktive Realms ohne eigenes Backup-Tooling

## Was solide ist

| Bereich | File-Reference |
|---|---|
| OAuth/OIDC-Core, PKCE-erzwingend, Refresh-Reuse-Detection mit Chain-Revocation | `Modgud.Infrastructure/OpenIddict/OpenIddictExtensions.cs` |
| Per-Realm Signing-Keys + Cert-Rotation-Overlap | OpenIddictExtensions + Realm-Domain |
| **DCR (RFC 7591)** — Triple-Opt-in, `[unverified]`-Marker, GC | `Modgud.Authentication/Api/Account/`, `Authorization/OAuth/` |
| 2FA-Breite: TOTP, EmailOTP, Recovery-Codes, Passkeys, Magic-Link | `Modgud.Authentication/Api/Account/` |
| Tenant-Isolation tief verdrahtet (`TenantedSessionFactory` + AsyncLocal) | `Infrastructure/Marten/TenantedSessionFactory.cs` |
| GDPR echt umgesetzt (Event-Masking + ArchiveStream) | `Authentication/Gdpr/GdprService.cs` |
| JsEval gefuzzt (834 Security-Tests + Depth/Length-Caps) | `MembershipSecurityTests.cs` |
| Security-Hardening-Track-Record (33 Findings closed) | `dev-docs/security-hardening.md` |

In **DCR-Sauberkeit**, **2FA-Modalitätsbreite** und **Hardening-Detail
für die Codebase-Größe** schlägt Modgud mehrere kommerzielle
Produkte.

## Roadmap (Audit-Findings → Followup-Aktionen)

| # | Punkt | Severity | Detail-Page | Status |
|---|---|---|---|---|
| 1 | OpenTelemetry / Metrics / Tracing | HIGH | — (shipped — see [Observability](/operate/observability)) | ✅ **DONE 2026-05-13** (Phase 1-5b) |
| 2a | Deployment-Hygiene (DataProtection persistent + Wolverine-Mode-Toggle) | HIGH | [ha-multi-instance](./ha-multi-instance) | ✅ **DONE 2026-05-13** |
| 2b | Echte HA / Multi-Instance (Cross-Instance Pub/Sub, Distributed Caches) | HIGH | [ha-multi-instance](./ha-multi-instance) | ⏸ Deferred — braucht echtes Multi-Box-Setup zum Testen |
| 3 | Realm-Backup / Restore / DR-Tooling (N Tenant-DBs) | MEDIUM | [realm-backup-restore](./realm-backup-restore) | Captured |
| 4 | Enterprise-SSO: SAML 2.0 + LDAP/AD-Federation | MEDIUM | [enterprise-sso-saml-ldap](./enterprise-sso-saml-ldap) | SAML 2.0 SP ✅ **DONE 2026-05-28** (PR #17 `8fc3df0`) · LDAP/AD weiter Captured |
| 5 | Brute-Force Visibility (Login-Alerts + manuelle IP-Blacklist) | MEDIUM | [login-alerts-ip-blacklist](./login-alerts-ip-blacklist) | Captured (2026-05-07) |
| 6 | Per-Realm Branding / Theming | LOW | [white-label-customization](./white-label-customization) | ✅ **DONE Phase 1 2026-05-13** ([Branding](/plattform/branding), [Asset Library](/plattform/assets), [Pages Beta](/plattform/pages)) |
| 7 | HSM / KMS Integration für Signing-Keys | LOW | (offen — siehe Audit-Note unten) | Captured-here |
| 8 | Realm-Provisioning Storage-Quota | LOW | (offen — siehe Audit-Note unten) | Captured-here |
| 9 | Bulk-User-Import / Export | LOW | (offen) | Captured-here |
| 10 | Step-up-Authentication via `acr_values` | LOW | (offen) | Captured-here |
| 11 | Risk-based / Adaptive Authentication | LOW | (offen) | Captured-here |
| 12 | i18n > DE+EN | LOW | (offen) | Captured-here |
| 13 | Compliance-Cert-Vorbereitung (SOC2/ISO27001) | LOW | (offen) | Captured-here |

### Notes zu noch nicht-eigenständig-erfassten Punkten

**#7 HSM / KMS** — Signing-Keys liegen als PFX auf dem Filesystem mit
`0600`. Auth0/Zitadel können in AWS KMS / Azure Key Vault. Für
Single-Tenant-Self-Host wahrscheinlich nie nötig; wird relevant wenn
Modgud jemals als Managed-Service angeboten wird.

**#8 Realm-Provisioning Quota** — `RealmProvisioningService` legt eine
neue Postgres-DB an. Ein kompromittierter Control-Plane-Admin kann
DBs schöpfen bis das Filesystem voll ist. Soft-Limit + Storage-Cap
fehlt. Mitigation derzeit: nur `realm:admin` darf provisionieren +
Audit-Event ist da.

**#9 Bulk-Import/Export** — `Modgud.Authentication/Api/Admin/`
hat nur per-User-CRUD. Ein einmaliges Migration-Bedürfnis ("alte
Identity-Datenbank → Modgud") wird das triggern.

**#10 Step-up** — `acr_values` + `amr`-Claim-driven Re-Auth fehlt
protokoll-getrieben. `TwoFactorEnforcementMiddleware` macht heute
einen pauschalen Grace-Check; aber wenn eine App sagt „für DIESEN
Endpoint will ich frische 2FA" — nicht möglich.

**#11 Risk-based Auth** — Geo-IP, Impossible-Travel,
Behavioral-Heuristics. Auth0/Okta haben das als Verkaufsargument.
Wir nicht. Login-Alerts (#5) ist die NAT-safe Vorstufe davon.

**#12 i18n** — `public/i18n/de.json` + `en.json`. Für unsere
Cocoar-Apps ausreichend, für „international ausrollen" zu wenig.

**#13 Compliance-Certs** — wir machen das nicht. Wäre relevant wenn
ein Enterprise-Kunde es fordert. Aufwand: Mannmonate, nicht Tage.

## Vergleichstabelle (1–5)

| Dimension | Modgud | Keycloak | Auth0 | Zitadel |
|---|---:|---:|---:|---:|
| OAuth/OIDC-Standards-Coverage | 4 | 5 | 5 | 5 |
| Token-Lifecycle (Rotation, Reuse-Detection) | 4 | 4 | 5 | 5 |
| DCR (RFC 7591) | **4** | 3 | 4 | 4 |
| Password + Lockout | 4 | 4 | 5 | 4 |
| 2FA Breadth | **5** | 4 | 5 | 5 |
| External IdP Federation (OIDC) | 3 | 5 | 5 | 5 |
| SAML / LDAP / AD | **3** (SAML 2.0 SP ✅ seit 2026-05-28, PR #17; LDAP/AD weiter offen) | 5 | 5 | 4 |
| RBAC Granularity | 4 | 4 | 4 | 5 |
| ABAC / Policy-Engine | 2 (extern) | 3 | 4 | 4 |
| Multi-Tenancy-Modell | 4 (DB-per-Realm) | 4 | 5 | 5 |
| Realm-Provisioning | 4 | 3 | 5 | 5 |
| Audit-Log | 4 | 4 | 5 | 5 |
| Observability (Metrics/Tracing) | **1** | 4 | 5 | 5 |
| Backup/Restore-Story | **1** | 3 | 5 | 5 |
| HA / Multi-Instance | **1** | 5 | 5 | 5 |
| Key-Management (HSM/KMS) | 2 | 4 | 5 | 5 |
| Admin-UI Completeness | 3 | 4 | 5 | 4 |
| Docs Quality | 4 | 4 | 5 | 4 |
| Branding/Theming per Tenant | 2 | 4 | 5 | 4 |
| i18n | 2 | 5 | 5 | 5 |
| Compliance-Certs | 1 | 3 | 5 | 4 |
| GDPR Self-Service | 4 | 3 | 4 | 5 |
| Hardening Track-Record | **5** (für die Größe) | 4 | 5 | 4 |
| **Gewichteter Eindruck Core-IdP-Funktion** | **3.5** | 4.1 | 4.7 | 4.4 |

## Verdict

**Würde ich meinen eigenen SaaS-Launch dem hier anvertrauen?** —
**Ja, unter Bedingungen.**

**JA, wenn:**

- Owner == Operator
- Eine Box (oder VM + Warm-Standby)
- <50 Realms, <100k User
- Eigene Apps, OIDC-only Clients
- Du eine eigene Backup-Pipeline akzeptierst als Followup
- Du `OpenTelemetry`-Wiring als Tag-1-Followup planst

**NEIN, wenn:**

- SAML/LDAP-Enterprise-Kunden onboarden willst → Keycloak
- HA mit ≥2 Replicas brauchst → Zitadel oder Keycloak+Redis
- SOC2-Audit fahren musst → Auth0/Zitadel-Cloud
- Als IdP-Produkt für fremde Firmen verkaufen willst → die werden
  nach SCIM/SAML/Audit-Cert fragen die du nicht hast

## Bottom line

Es ist nicht Keycloak. Es ist auch nicht Auth0. Es ist ein scharf
zugeschnittener OAuth/OIDC-Server für einen Owner-Operator mit
Cocoar-internen Apps und MCP-Clients. In dieser Nische schlägt es
mehrere kommerzielle Produkte. Außerhalb dieser Nische sind es
ehrlich gesagt 2–3 Mannmonate Arbeit hinten dran — aber das ist die
richtige Bauphilosophie für **diesen** Anwendungsfall.

Die Roadmap-Tabelle oben ist das Backlog. Status pro Item wird in den
jeweiligen Detail-Pages (oder hier inline für Stubs) gepflegt. Wenn ein
Item ausimplementiert ist, wandert es analog zur DCR-Promotion
(siehe `dcr-for-mcp-clients` → `admin/dynamic-client-registration`)
in die öffentliche Doku.
