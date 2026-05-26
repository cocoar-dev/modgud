# Per-App login customization (routing + form-builder)

> **Status:** Designkonsens 2026-05-12. Nicht implementiert.
> **Why:** Modgud zentralisiert Login — ein Realm, eine
> Login-Seite, alle Apps des Realms teilen sich denselben UI. Sobald
> derselbe Cocoar-Kunde mehrere eigene Produkte/Apps fährt (alpha-blog,
> beta-shop, gamma-crm, event-tree, ...) bricht das Marketing-mässig:
> Jedes Produkt soll wie es selbst aussehen, nicht wie eine generische
> IdP-Seite mit eingeklemmtem Logo. Auth0 löst das mit "Universal
> Login per Application", Keycloak mit Themes + Realm-pro-App-Tricks.
> Diese Note skizziert den Cocoar-Pfad: **App-Context-aware Routing
> plus Form-Builder mit fertigen Bausteinen**.

## Abgrenzung zu white-label-customization

[white-label-customization](./white-label-customization) deckt
**per-Realm**-Branding ab — Logo/Farben/Brand-Name für *einen* Mandanten.
Diese Note baut darauf auf: per-App-Branding ist die feinere Schicht
unter dem Realm. Reihenfolge im Phasen-Plan: erst Realm (Standard-Ask),
dann App (Standard-Ask Nummer zwei).

## Routing — wie die App-Identität auf der Login-Seite landet

Drei Pfade in den Login, **alle convergieren auf denselben Slot**
`HttpContext.Items["AppContext"]`. Welcher Pfad gerade greift hängt
davon ab wie der User reinkommt:

| Pattern | Beispiel-URL | Wo erkannt | Setup-Cost |
|---|---|---|---|
| **Subdomain** | `event-tree.cocoar.dev/login` | Host-Header | DNS-Eintrag (CNAME auf canonical IdP) + Wildcard-Cert (haben wir) |
| **Subpath** | `auth.example.com/app/event-tree/login` | URL-Präfix | Null — eine Code-Route reicht |
| **Implicit (OAuth-Flow)** | `auth.example.com/connect/authorize?client_id=event-tree-spa` | `client_id` → OAuthClient.AppIds → App | Nichts — heute schon Daten da |

Der wichtigste Punkt: **keine komplett-getrennten TLDs**. Cookie-SSO
funktioniert nur wenn alle App-Pfade unter einem gemeinsamen Cookie-Parent
liegen (z.B. `.cocoar.dev` oder `auth.example.com`). Echte
unabhängige TLDs (`event-tree.com` + `alpha-blog.com`) brechen
Browser-SSO ohne zusätzlichen Silent-Renewal-Dance — nicht in Scope.

## Schichtenbild

```
RealmMiddleware          — Host → TenantId      (unverändert)
  ↓
AppContextMiddleware     — Host oder URL-Prefix → AppId  (neu)
  ↓
OpenIddict-Pipeline      — bei /connect/authorize: client_id → AppId
                           ergänzt App-Context falls noch nicht gesetzt
  ↓
LoginView                — liest App-Context, rendered branded/Form
```

Die Middleware-Reihenfolge ist wichtig: Realm zuerst (Tenant-Scoping
muss stehen bevor irgendwas DB-Lookups macht), App danach.

## App-Entity-Erweiterungen

```csharp
public record App(
    // ... bestehend (Slug, DisplayName, Permissions, ...)

    // Subdomain-Pattern — n optional. Hostnames müssen DNS-resolvable
    // sein und im selben Realm wie die App liegen.
    string[]? LoginHostnames,

    // Subpath-Pattern — defaults zu App.Slug. Routet
    // /app/<value>/login → App-branded login.
    string? LoginPath,

    // Branding-Tokens (gleiche Form wie RealmTheme)
    AppBranding? Branding,

    // Form-Schema — siehe Form-Builder-Section
    LoginFormSchema? LoginForm);
```

`LoginHostnames` und `LoginPath` sind beide optional. Eine App kann
keine, eine, oder beide haben. Eine App ganz ohne eigenes
Routing-Pattern wird nur über `client_id` im OAuth-Flow App-branded —
direkter Besuch von `/login` fällt auf Realm-Default zurück.

## Form-Builder — der UX-Vision-Kern

Statt jeder App freistehendes HTML oder rohes CSS zu erlauben (XSS,
A11y-Bruch, Phishing-Surface), kriegt der Admin einen **Block-Editor**
mit vorgefertigten Bausteinen. Jeder Baustein weiß wie er sich
wired-up — kein Backend-Glue notwendig, A11y und Localization
eingebaut, Security garantiert.

### Block-Katalog (Vorschlag)

| Block | Was es ist | Konfigurierbar |
|---|---|---|
| `BrandHeader` | Logo + Titel + optional Subtitle | Logo-Asset, Heading-Text, Sub-Text |
| `UsernameField` | Username- oder Email-Input | Label, Placeholder, optional Auto-Focus-Default |
| `PasswordField` | Password-Input mit Show/Hide-Toggle | Label, Placeholder |
| `RememberMeCheckbox` | "Angemeldet bleiben"-Checkbox | Label, Default-State |
| `LoginButton` | Submit-Button | Label, Variant (primary/secondary) |
| `ForgotPasswordLink` | Link zu `/forgot-password` | Label |
| `Divider` | "oder"-Trenner | Label-Text |
| `MagicLinkButton` | Magic-Link-Login-Trigger | Label, Icon |
| `PasskeyButton` | WebAuthn-Login | Label, Icon |
| `ExternalProviderButton` | Login via OIDC-Provider (Google etc.) | Provider-Id, Label, Icon |
| `LegalFooter` | Privacy/ToS/Support-Links | Link-Konfigurationen |
| `Spacer` | Vertikaler Raum | Höhe |
| `Note` | Statischer Hinweis-Text | Text, Variant (info/warning) |

Mehr Bausteine kommen dazu wenn sich Customer-Asks häufen. **Was es
nicht gibt:** Custom-HTML-Block. Wenn ein Customer wirklich raw HTML
braucht, ist das ein anderes Gespräch (eigener Tier 3 — siehe
white-label-customization).

### Schema-Form

```jsonc
{
  "version": 1,
  "blocks": [
    { "kind": "BrandHeader", "logoAssetId": "abc123", "heading": "Sign in to Event Tree", "subText": "Plan smarter, ship faster." },
    { "kind": "UsernameField", "label": "Email or username", "autoFocus": true },
    { "kind": "PasswordField", "label": "Password" },
    { "kind": "RememberMeCheckbox", "label": "Stay signed in", "defaultChecked": false },
    { "kind": "LoginButton", "label": "Sign in", "variant": "primary" },
    { "kind": "ForgotPasswordLink", "label": "Forgot password?" },
    { "kind": "Divider", "label": "or" },
    { "kind": "MagicLinkButton", "label": "Email me a sign-in link" },
    { "kind": "PasskeyButton", "label": "Sign in with passkey" }
  ]
}
```

Validierung im Backend: Schema-JSON-Schema-validiert, jeder Block
gegen seinen Typ-Schema. Unbekannte Blocks werden zurückgewiesen
(forward-compat: kein silent-ignore weil das verwirrend wäre wenn ein
Admin später ein neueres UI-Build deployed).

### Rendering im Frontend

`LoginView.vue` wird zu einer **Block-Renderer-Komponente**:

```vue
<template>
  <div class="login-form">
    <component
      v-for="block in form.blocks"
      :key="block.id"
      :is="blockRegistry[block.kind]"
      v-bind="block"
      @submit="handleSubmit"
    />
  </div>
</template>
```

Jeder Block ist eine Vue-Komponente die per `useLoginContext()`
auf das gemeinsame Form-State zugreift (Username, Password, etc.) —
egal wo er platziert ist, "der" Login-Button submitted "den" Form.

### Default-Schema

Realms ohne explizites Form-Schema und Apps ohne Override kriegen
ein **System-Default-Schema**, das genau dem aktuellen
hartkodierten `LoginView.vue` entspricht — also kein Regression-Risk
für bestehende Realms.

### Admin-UI

Der Form-Builder selbst ist eine Drag-Drop-Liste:
- Linke Spalte: verfügbare Blocks
- Rechte Spalte: zusammengestellter Form, drag-to-reorder
- Click auf Block → Sidebar mit Block-Konfiguration
- Live-Preview neben dem Editor (gleicher Renderer wie LoginView selbst)

Optional späterer Tier: Visual-Tree-View, conditional Blocks
(nur zeigen wenn Realm OIDC-External-Login hat), etc. Out of scope
für v1.

## Wichtige Nicht-Änderungen

**Issuer-Claim bleibt realm-canonical.** Egal über welchen App-Pfad
der User reinkommt — der ausgestellte Token trägt
`iss: https://auth.example.com/` (oder die Realm-Hostname, je nach
Setup). Wäre der Issuer App-spezifisch, würde RS-Token-Validation
zerbrechen, weil derselbe Realm dann pro App ein anderes
Discovery-Document publishen müsste. App-Pfad ist **nur Login-UX**,
nicht Protocol-Anker.

**OAuth-Endpoints bleiben auf canonical Pfaden.** `/connect/authorize`,
`/connect/token`, `/connect/userinfo`, `/.well-known/...` werden
**nicht** unter `<app-subdomain>/` oder `/app/<slug>/connect/...`
gespiegelt. Standard-OIDC-Clients erwarten die Endpoints am
Discovery-Document-Pfad — der ist realm-fix.

**Auth-Cookie wird auf den gemeinsamen Cookie-Parent gesetzt**
(`.cocoar.dev`), sodass Login auf `event-tree.cocoar.dev` und
nachgelagerter Visit von `alpha-blog.cocoar.dev` dieselbe Session
sehen. Heute schon so wenn beide Hostnames als Realm-Domains
registriert sind — bleibt unverändert.

## Edge-Cases die wir entscheiden müssen

### 1. Direct-/login-Visit auf App-Subdomain ohne client_id

User tippt `event-tree.cocoar.dev/login` manuell ein. App-Context
ist gesetzt (aus Host), aber es gibt keinen target-Client. Drei
Optionen:

- **(a) App-branded Login + Liste der App-Clients** — "Wo willst
  du dich anmelden? [Event Tree Web] [Event Tree Mobile]"
- **(b) Auto-Redirect zu einem als `IsPrimary` markierten Client**
  der App. App-Modal kriegt einen "Primary Client"-Pointer.
- **(c) 404 / nur erlaubt im OAuth-Flow.**

Empfehlung: **(b)**, weil das den 95%-Use-Case ohne Verwirrung
trifft. App ohne Primary-Client fällt auf (a) zurück.

### 2. App ohne Routing-Konfiguration

Eine App ohne `LoginHostnames` und ohne `LoginPath` ist **nur über
den OAuth-Flow** App-branded erreichbar. Direkter `/login`-Visit
zeigt Realm-Default. Das ist die default-Position für Apps die kein
extra Setup wollen.

### 3. Branding-Cascading

Wenn ein Realm ein Theme hat und eine App in dem Realm ihr eigenes
hat: **App-Theme overridet Realm-Theme.** Per-Feld, nicht
all-or-nothing — wenn App nur `PrimaryColor` overridet aber kein
Logo, dann Realm-Logo + App-Farbe.

### 4. SSO über Login-Hostnames

Cookie auf `.cocoar.dev` deckt alle `*.cocoar.dev`-Subdomains ab.
Damit funktioniert SSO automatisch wenn ein User auf
`event-tree.cocoar.dev` log't und gleich danach
`alpha-blog.cocoar.dev` besucht — beide sehen dieselbe Session.
Voraussetzung: alle App-Hostnames sind Subdomains desselben Parents.

## Migration / Opt-in

Bestehende Realms bleiben unangetastet. Alles funktioniert wie
heute. Sobald Admin einer App eine `LoginHostname` oder ein
`LoginForm`-Schema einträgt, greift die neue Pipeline. Default-Schema
== aktuelle hartkodierte LoginView, kein Regression-Risk.

## Phasen-Plan

| Phase | Liefert | Aufwand |
|---|---|---|
| **1** | AppContext-Middleware (Host + Subpath), App-Branding-Tokens, OAuth-Flow-Integration via client_id, Fallback-Cascade | 3-4 Tage |
| **2** | Form-Builder Schema + Renderer im Frontend, Default-Schema, Block-Registry mit Initial-Set | 5-7 Tage |
| **3** | Admin-Form-Builder-UI mit Drag-Drop + Live-Preview | 5-7 Tage |
| **4** | Erweiterte Blocks (ExternalProviders, conditional Visibility) | nach Bedarf |

## Risiken

1. **Schema-Versionierung.** Wenn wir Blocks deprecaten oder Schema-Form
   ändern, brauchen wir Migration-Strategy. Optionen: Schema-Version
   prüfen am Lookup + auto-upgrade, oder hartes "v1 → v2"-Migration-Script.
2. **Vorschau im Admin = LoginView selbst.** Ein zweites Renderer-Build
   driftet sofort. Live-Preview im Admin sollte das echte
   LoginView in einem iframe rendern mit dem Editor-Schema injiziert.
3. **Localization der Blocks.** Jeder Block braucht `label`/`text`-Props
   die i18n-fähig sind. Wenn Admin einen Custom-Label setzt: ist das
   ein einzelner String (current-Locale) oder ein i18n-Bundle?
   Tier-1: Single-String, Locale-Übersetzung kann der Admin nicht.
   Tier-2: i18n-Bundle pro Block-Prop.
4. **Phishing-Risiko bei Custom-Hostnames.** Wenn Admin
   `event-tree.cocoar.dev` registriert und einen anderen Customer
   `event-tree-fake.cocoar.dev` registriert, könnte Letzterer
   Phishing für die echte App betreiben. DNS-Vergabe ist Cocoar-Admin
   Sache — App-LoginHostname-Eintrag durch Tenant-Admin muss
   nicht-konfliktfrei mit System-DNS sein. Validation am Eintrag
   (gleicher Realm muss DNS-Ownership beweisen).

## Referenzen

- [white-label-customization](./white-label-customization) — Per-Realm-Branding (Vorgänger-Schicht)
- OAuth 2.0 RFC 6749 — `client_id` als Identifikator pro App
- OIDC Discovery — Issuer-Constancy-Constraint
- Auth0 Universal Login (Konzept-Vergleich)
- Keycloak Themes (Konzept-Vergleich)
