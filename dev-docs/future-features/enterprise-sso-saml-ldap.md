# Enterprise SSO — SAML 2.0 + LDAP/AD-Federation

> **Status:** Roadmap-Item, **kontextabhängig**. Designspace captured
> 2026-05-13.
> **Why:** Modgud federiert heute nur über OIDC (Entra, Okta, …).
> Enterprise-Kunden fordern oft SAML 2.0 (alte Identity-Provider,
> Salesforce, ServiceNow) und/oder LDAP/AD-Direktanbindung. Beides
> fehlt. Audit-Finding aus
> [production-readiness-audit-2026-05-13](./production-readiness-audit-2026-05-13)
> Punkt #4.

## Status quo

OIDC-External-Auth ist live (`Authentication/Api/ExternalAuth/`):
DynamicOidcSchemeManager, Per-Realm-Provider-Configuration, AMR-Claim-
Preservation für Federated-MFA. Sauber gebaut.

Was fehlt:

- **Kein SAML 2.0 SP** (wir als SAML Service-Provider gegenüber einem
  Customer-IdP)
- **Kein SAML 2.0 IdP** (wir als IdP für SAML-only Apps wie alte
  Salesforce/ServiceNow-Installationen)
- **Kein LDAP/AD-Bind-Login** (Direct-Bind gegen Customer-AD)
- **Kein SCIM 2.0 Provisioning-Endpoint** (User-Provisioning aus
  HR-System à la Workday)

## SAML 2.0 — Designspace

### Use-Case A: SAML SP (wir konsumieren Customer-SAML-IdP)

Häufigster Enterprise-Case. „Customer X hat ADFS, will SSO ohne uns
in ihre Cloud zu lassen."

Optionen:

- **`Sustainsys.Saml2`** — Open-Source, ASP.NET-Core-tauglich, gut
  gepflegt. Empfohlen
- **`ITfoxtec.Identity.Saml2`** — Alternative, kommerziell-lastig
- **Eigenbau** — Nein, SAML-Signing/Encryption ist zu fehleranfällig
  zum selbst-schreiben

Integration analog zu OIDC-External-Auth:

- Pro Realm konfigurierbar: SAML-IdP-Metadata-URL, SP-EntityID,
  Signing-Cert-Thumbprint, ACS-Endpoint
- DynamicSamlSchemeManager analog zu DynamicOidcSchemeManager
- Attribute-Mapping (SAML-Claims → ApplicationUser-Properties)
- Same AMR-Preservation für Federated-MFA-Erkennung (SAML
  `AuthnContextClassRef` → AMR)

### Use-Case B: SAML IdP (wir gegenüber SAML-only Apps)

Seltener, aber: alte interne Apps die nur SAML können (z.B. Confluence
< 7, alte SharePoint). Customer will: „Modgud als Single
IdP für alles — auch das alte SAML-Zeug."

OpenIddict ist OAuth/OIDC-only. Für SAML-IdP-Funktion bräuchten wir:

- Separates SAML-IdP-Library (Sustainsys hat IdP-Mode auch)
- ODER ein SAML→OIDC-Bridge-Pattern: Customer-App spricht SAML mit
  einem dedizierten SAML-Bridge-Service der intern OIDC gegen
  Modgud fährt
- Bridge-Pattern ist einfacher (kein eigener IdP-Code), aber Extra-
  Hop und Extra-Cert-Management

**Empfehlung:** Use-Case A zuerst (häufiger), Use-Case B nur on-demand.

## LDAP/AD — Designspace

### Optionen

- **A — Direct-Bind-Login**: User loggt sich mit AD-Credentials ein,
  Modgud bind't direkt gegen Customer-DC, validiert,
  erstellt/aktualisiert lokalen User-Record. Customer-Passwörter
  niemals persistiert
- **B — LDAP-Sync (one-way)**: Periodischer Job zieht User aus LDAP
  in Modgud-Tenant-DB. Login dann lokal mit gehashter Kopie.
  Schlechter (PW-Drift, Security-Profil schlechter), aber funktioniert
  auch wenn DC nicht 24/7 erreichbar
- **C — LDAP-Provisioning (SCIM-Style)**: Modgud provided
  Provisioning-Endpoint, Customer-Tool pushed User rein. Eigentlich
  SCIM, nicht LDAP

**Empfehlung:** A für echtes „SSO mit AD". `Novell.Directory.Ldap.NETStandard`
oder `System.DirectoryServices.Protocols` als Library.

### Sicherheits-Aspekte

- LDAP-Bind über `ldaps://` enforced (nicht plain `ldap://`)
- Per-Realm LDAP-Config in Tenant-DB, **encrypted** (wie External-OIDC-
  Client-Secrets heute auch). DataProtection-Pattern wiederverwenden
- Service-Account-Credentials für Initial-Search-Bind separate von
  User-Bind
- Audit-Event pro LDAP-Login (Success/Failure mit DN)

## SCIM 2.0 — Designspace

Optional, aber häufig zusammen mit SAML/LDAP gefordert.

- **SCIM-Endpoint**: `/scim/v2/Users`, `/scim/v2/Groups` POST/PUT/PATCH
- Customer-HR-System (Workday, BambooHR) pushed User rein
- Provisioning ohne Login-Event — User existiert bevor er sich erstmal
  einloggt
- Mapping SCIM-Attributes → ApplicationUser

Library: keine etablierte für ASP.NET Core. Custom-Implementation
~3 Tage für Minimal-RFC-7644-Compliance.

## Effort

- SAML SP (Use-Case A): **~5 Tage** (Library-Integration + Per-Realm-
  Config + Dynamic-Scheme + Tests)
- SAML IdP (Use-Case B): **~7 Tage** ODER **~2 Tage** als Bridge
- LDAP Direct-Bind: **~4 Tage** (Bind-Helper + Per-Realm-Config +
  Login-Flow + Tests)
- SCIM 2.0 Endpoint: **~3 Tage** minimal, **~5-7 Tage** vollständig
- **Komplett-Enterprise-SSO-Paket:** ~3 Wochen

## Was wir bewusst NICHT machen (zumindest jetzt)

- **Kein Kerberos / SPNEGO**. Nische der Nische. On-demand only
- **Kein WS-Federation**. Außer ADFS-Legacy in ganz alten Setups praktisch tot

## Trigger

Dieser Punkt ist **strikt customer-getrieben**, nicht prophylaktisch.
Geplant wenn:

- Erster Sales-Prospect SAML als Hard-Requirement nennt
- Erster Customer mit ADFS-Footprint Onboarding will
- Eines der Cocoar-Apps explizit als „enterprise-ready" positioniert
  werden soll

Vor diesem Trigger: zur Sales-Antwort gehört „wir machen OIDC, das ist
modern; SAML können wir bauen wenn das ein Deal-Breaker ist". Niemand
investiert 3 Wochen für „falls jemand fragt".

## Marktvergleich

| | Modgud | Keycloak | Auth0 | Zitadel |
|---|---|---|---|---|
| SAML SP | ❌ | ✅ | ✅ | ✅ |
| SAML IdP | ❌ | ✅ | ✅ | ❌ |
| LDAP/AD | ❌ | ✅ | ✅ | ✅ (Beta) |
| SCIM | ❌ | ❌ (Plugin) | ✅ | ✅ |
| OIDC | ✅ | ✅ | ✅ | ✅ |
| Kerberos | ❌ | ✅ | ❌ | ❌ |

Wir sind **OIDC-modern**, **SAML-/LDAP-blind**. Bewusste Wahl, solange
Use-Case nicht da ist.
