---
title: Login-Providers admin-UI refactor — single-modal + quick-map
---

# Login-Providers admin-UI refactor — single-modal + quick-map

> **Status:** Designed in detail during the SAML federation wave on 2026-05-27 (decisions captured in chat). Phase 1 (SAML provider support via the existing two-step UI) shipped with the SAML wave. Phase 2 (this refactor) deferred to its own wave.
> **Why:** The current "create with minimal fields → open detail modal → edit everything else" pattern is an outlier vs Auth0 / Keycloak / Okta which use a single modal that morphs based on the selected flavor. Plus the SAML config surface (metadata URL, attribute map, AMR mapping, refresh cadence) is larger than OIDC's and amplifies the friction of the current pattern.

## What changes

### Single-modal Add + Edit

One modal, used for both new and existing providers:

- Title bar shows the Display Name (or "Add Login Provider")
- **Flavor picker lives in the modal's header-actions slot** (shipped 2026-05-27 in [[ui-modal-header-actions-slot]]) — disabled in Edit mode (Type / Flavor are immutable after create), active in Add mode where switching morphs the body.
- Tabs: Allgemein, Verbindung, User-Update-Script, Verknüpfung & Richtlinien, plus a new **Attribute & Membership** tab specifically for SAML
- Single Save button at the modal footer (the existing `useUI` footer pattern)
- Default Enabled state on Save = false (sicherheits-default; admin enables after smoke-test). UI offers an "Enabled" toggle in the Allgemein tab so an admin can opt in to direct-enable.

### Backend `CreateLoginProviderCommand` change

Currently accepts only `Flavor + DisplayName + Type + FlavorData`. To support the single-modal pattern (admin enters everything at once), the command needs to accept the full provider state in one go — equivalent to `UpdateLoginProviderRequest` shape.

Migration approach: extend `CreateLoginProviderCommand` with optional fields that fall back to flavor defaults when null. Existing two-step UI keeps working (omits the optional fields, gets defaults; then issues the Update command). The new UI sends everything in one Create call.

### Quick-Map UI for groups (SAML Mode B)

The Identity-Hub group mapping decision ([[project-identity-hub-vs-federation-proxy-open]]) settled on JsEval auto-membership scripts as the single mechanism. But customers shouldn't have to write JsEval for the 80% case where they just want "if IdP group X then Modgud group Y".

Quick-Map UI is a generator:

1. Admin clicks "Add mapping"
2. Pick which claim (dropdown sourced from configured AttributeMap logical names, defaulting to `groups`)
3. Pick which value (free-text or dropdown if recently-seen values exist)
4. Pick which Modgud Group (typeahead against the realm's group catalog)
5. UI generates the corresponding line in the membership script: `claims.groups?.includes("corp-engineering") ? ["modgud-group-id"] : []`
6. Power-user mode: edit the generated script directly. Quick-Map detects when the script no longer matches a recognised pattern and warns ("script is hand-edited; Quick-Map view may be incomplete").

### Multi-IdP UX touchpoints

The flavor picker dropdown becomes larger with SAML flavors added (Generic SAML, EntraID SAML, ADFS SAML on top of EntraID + Generic OIDC). With 5+ entries it should:

- Group by Type (OIDC providers vs SAML providers) with a section divider
- Add a search-as-you-type filter when N>5

(Adjacent to but distinct from [[multi-idp-login-ux]], which is the *login-page* picker for end users; this is the *admin add-provider* picker.)

## Out of scope

- The bigger product question of Identity-Hub vs Federation-Proxy is captured at [[project-identity-hub-vs-federation-proxy-open]] and *intentionally not revisited* here. This refactor keeps the Identity-Hub model.
- LDAP / Kerberos flavors are not added here — they would gain UI when their respective protocol slices land.

## Effort estimate

- Single-modal refactor (combine Add + Edit, header-actions slot wiring, backend Create command extension): **~2 days**
- Quick-Map UI for groups + JsEval bidirectional sync: **~1.5 days**
- Flavor-picker grouping + search: **~0.5 days**

**Total: ~4 days focused frontend work**, with backend side ~0.5 day of those.

## Trigger to start

Default: **on customer-feedback friction.** The current Phase-1 two-step is functional; admins can configure SAML providers, save, edit, enable, smoke-test against EntraID / ADFS / simplesamlphp. The polish from this refactor is worth shipping *after* we have first-customer signal about which parts of the flow are confusing in practice.

Concrete signals that would advance it:
- Customer onboarding session where the admin gets stuck on the two-step pattern
- Sales demo where the Add-Provider screen looks "less polished than Auth0"
- Internal Modgud team-pain reaches some threshold during day-to-day config
