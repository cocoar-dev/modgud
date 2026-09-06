# Finish the customization core before the page builder

**Status:** Accepted — implementation order; status recorded 2026-08-02 · **Decided:** 2026-08-02

## Context

Modgud already has realm branding, an asset library, application-specific settings, and a feature-gated page builder. The foundation, however, is not yet consistently finished: fixed auth surfaces do not use branding everywhere, application overrides have gaps in inheritance and reset, asset references are not fully protected, and email customization has so far been reduced to a handful of values.

The page builder is not yet generally released. Most white-label requirements do not need a freely editable layout, but a consistent fixed surface with logo, colors, text, login methods, and branded emails.

## Decision

The **Customization Core without the page builder is implemented, tested, documented, and released first**.

The page builder remains optional, disabled by default, and outside the acceptance criteria of the Customization Core. It is continued afterwards as an extension for fully custom layouts.

The shared resolution model remains:

```text
Built-in → Realm → Application → optionally OAuth Client
```

Customization of presentation and technical application policies are treated separately in the information architecture.

## Scope of the Customization Core

### 1. Consistent branding

Realm defaults and application overrides for product name, logo variants for light/dark backgrounds, favicon, safe color/design tokens, an optional login background, and footer, support, privacy, imprint, and terms-of-service links.

The effective branding applies across all fixed surfaces: login, registration, forgot/reset password, magic link, email verification, consent, device verification, MFA/secure setup, logged out, profile, and the admin shell.

The implementation uses a shared fixed auth shell. Security steps and their ordering remain host-controlled.

### 2. Unambiguous inheritance

Every application field explicitly distinguishes:

1. inherit from realm
2. force built-in or empty
3. set its own value

This applies in particular to booleans, lists, and asset references. The UI shows the effective value and its source.

### 3. Application experience

To be completed:

- logo and favicon in the application UI
- correctly removing an application subdomain
- cleanup of host mapping and settings when an application is deleted
- complete asset reference protection
- selection, ordering, and visibility of login providers and login methods per application
- application-specific text and legal links
- preview in the effective application context

Registration, sessions, native grants, and DCR/CIMD remain separate technical policies and are not presented as visual customization.

### 4. Email customization without a layout builder

Initially, a fixed, responsive, and tested email layout is used.

Customizable are product name, logo, colors, sender display name, controlled reply-to, support contact, footer, legal links, subject, preheader, explanatory text, button labels, and localized variants, initially for German and English.

OTP, action URL, expiry time, and security notices remain typed, non-removable building blocks. Every email gets an HTML and a plain-text version.

Sending is given an explicit context with realm, optional application, template, locale, and recipient. Resolution must not depend solely on the current HTTP host, so that background jobs, the outbox, and delayed sending remain correctly branded.

Template variables are typed and permitted based on context; ordinary values are HTML-escaped, URLs are validated separately.

### 5. Asset library

Before the core is released:

- protect references from realm and application branding
- show "used by"
- support safe asset replacement
- prevent broken references
- offer matching categories/pickers for logo, favicon, and email

Page builder references will be added at the latest before its later release.

### 6. Preview, tests, and documentation

- realm/application preview
- desktop/mobile as well as light/dark backgrounds
- contrast warnings
- email preview and test send
- E2E coverage of all fixed auth pages
- tests for inherit, override, force default, reset, and delete
- align documentation and roadmap with the actual state

## Implementation order

1. fix inheritance, reset, and delete semantics
2. bring branding to all fixed surfaces via a shared auth shell
3. complete application branding and the application login experience
4. introduce an explicit email context and safe, centralized mail composition
5. implement realm and application email customization including preview/test send
6. finish asset integrity, E2E tests, and documentation
7. release the Customization Core
8. continue the page builder separately afterwards

## Non-goals of the core

- freely executable JavaScript
- arbitrary unsanitized HTML or CSS
- freely editable security transitions
- freely settable sender address without a verified domain
- fully free page or email layouts

## Consequences

- Customers get full white-labeling without the complexity of a builder.
- The page builder can later build on the same branding, asset, inheritance, and security foundations.
- Existing page builder code remains feature-gated; its further development does not block the core release.
- The first implementation steps prioritize correctness and consistency over new layout features.

## Implementation status (2026-08-02)

The fixed Customization Core has been implemented on branch `codex/customization-core`:

- realm-to-application inheritance for web/email branding, including clean reset
- application logo/favicon, safe asset references, origin clear, and delete cleanup
- shared branding across all fixed auth surfaces, plus safe legal links
- application-specific internal login, magic link, and an ordered OIDC/SAML allow list; enforced server-side at the entry points
- fixed responsive DE/EN email renderer with HTML+plain text, escaping, CR/LF protection, logo/color, subject prefix, preheader, footer, sender display name, and validated reply-to
- explicit application/OAuth client context, including on the central realm domain
- realm/application preview including contrast warning
- manifest export and documentation updated

Verification: API build, Vue type check, frontend production build, and in-app documentation build succeeded; 43 focused unit tests and 14 PostgreSQL integration tests passing. A real SMTP send was verified via Mailpit (HTML, plain text, branding, and URL escaping).

The page builder remains unchanged, beta/feature-gated. Free page and email layouts, as well as freely executable HTML/CSS/JavaScript, remain explicitly out of scope for this core.
