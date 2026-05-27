# SAML 2.0 federation

Modgud federates user authentication via SAML 2.0 — your customer keeps using their existing IdP (Microsoft Entra ID, ADFS, Okta, ...), Modgud receives the assertion, issues its own OIDC tokens to your downstream apps. Modgud always acts as **Service Provider (SP)** in this flow.

## Concepts

- **Service Provider (SP)** — the side that consumes a SAML assertion. That's Modgud.
- **Identity Provider (IdP)** — the side that authenticates the user. That's your customer's EntraID / ADFS / Okta tenant.
- **AuthnRequest** — the message Modgud sends to the IdP saying "please authenticate this user."
- **SAML Response (assertion)** — the message the IdP sends back, signed, with the user's identity + attributes.
- **ACS URL** (Assertion Consumer Service) — Modgud's endpoint that receives the SAML Response. Each Modgud SAML provider has its own ACS URL with the provider's GUID in the path.
- **SP metadata** — an XML document Modgud publishes that the IdP imports to establish trust. Contains our SP EntityID, ACS URL, and signing certificate.
- **IdP metadata** — the corresponding document on the IdP side. Modgud imports it to know where to send AuthnRequests and which signature to trust.

## Add a SAML provider

1. **Admin → Login-Provider → Provider hinzufügen.** A single modal opens
   with the flavor picker in the header and all tabs visible.
2. **Flavor** (header dropdown): pick one.
   - **SAML · Generic SAML 2.0** — vendor-neutral; admin fills in everything.
   - **SAML · Microsoft Entra ID (SAML)** — pre-fills the Microsoft claim
     URIs (`http://schemas.xmlsoap.org/...`) in the attribute map. Use this
     when the IdP is an EntraID Enterprise Application.
   - **SAML · Active Directory Federation Services (SAML)** — pre-fills AD
     claim URIs + UPN NameID conventions. Use for on-prem ADFS.

   Switching flavor in this modal swaps the Verbindung-tab schema and
   the pre-seeded attribute map; admin-typed Display Name / Description
   stay intact across switches.
3. **Allgemein** tab: enter Display Name (shown on the login page button)
   + optional Beschreibung. The SP-Metadata-URL + ACS-URL fields appear
   AFTER first save — Modgud needs to mint the provider id to derive them.
4. **Verbindung** tab: set either
   - **IdP Federation Metadata URL** — where the customer IdP publishes
     its metadata. EntraID publishes this on the Enterprise App's
     Single sign-on page as "App Federation Metadata Url".
   - **IdP Metadata XML** — paste the metadata XML directly. Use when
     the IdP isn't reachable from Modgud (firewalled on-prem ADFS).
5. **Erstellen.** Provider lands **disabled** (security default — admin
   enables explicitly after smoke-test). The modal stays open and
   transitions into Edit mode; the URL fragment updates to the new
   provider id and the SP-Metadata-URL + ACS-URL fields now show in the
   Allgemein tab with copy buttons.
6. Copy SP-Metadata-URL + ACS-URL from the Allgemein tab and paste them
   into the customer IdP setup (see [Wire the customer IdP](#wire-the-customer-idp)).
7. Once the IdP is wired and you've verified an SP-initiated login round
   trip, click the **Deaktiviert** badge in the modal's header to flip
   it to **Aktiviert**. Modgud refuses to enable a SAML provider without
   IdP metadata in FlavorData, so step 4 must succeed first.

::: tip You can enable on Create
If you already have the IdP metadata URL ready when you open the modal,
fill it in the Verbindung tab BEFORE clicking Erstellen and the provider
can land enabled immediately. The readiness gate (no metadata → no
Enabled) is enforced by the backend.
:::

## Wire the customer IdP

The IdP needs to know about Modgud as an SP. Two URLs to give them:

- **SP EntityID** = **SP metadata URL** — `https://<modgud-host>/saml/<provider-id>/sp-metadata`.
- **ACS URL** — `https://<modgud-host>/saml/<provider-id>/acs`.

Most IdPs (EntraID, Okta, ADFS) accept "import SP metadata" via the URL — preferred path, because metadata also carries our signing cert. If the IdP wants the cert as a file, download it from the SP metadata XML's `<X509Certificate>` element.

The `<provider-id>` is the GUID Modgud assigned when you created the provider — visible in the URL fragment when the detail modal is open. Each SAML provider has its own SP EntityID + ACS URL.

## Test users

Use the IdP's normal test users. For our `dev/saml-idp/` simpleSAMLphp dev IdP, the baked-in test users are `user1` / `user1pass` and `user2` / `user2pass`.

## What flows through

When a user signs in via the SAML provider, Modgud:

1. Validates the SAML Response signature against the IdP's signing certs (from the imported / fetched metadata).
2. Verifies the audience matches our SP EntityID.
3. Extracts NameID (→ Modgud `sub`) and attributes.
4. Applies the provider's **AttributeMap** to translate IdP-specific claim URIs (e.g. Microsoft's `http://schemas.xmlsoap.org/.../emailaddress`) to stable logical names (`email`).
5. Runs the **User-Update-Script** to derive `firstname`, `lastname`, `email`, `acronym` from the claims.
6. Links the external identity to an existing Modgud user (by email-domain match or auto-link rule) **or** creates a new user via JIT provisioning if enabled.
7. Signs the user into the Modgud cookie.

Group / role / permission membership stays under Modgud's control — see [Permission-Modell](../concepts/permissions) for the design rationale. Group-claim → Modgud-Group translation is handled by [auto-membership scripts on Groups](./groups#auto-membership-membership-scripts).

## Cert rotation

Two halves:

- **SP cert** (ours, per realm). Auto-generated on first SAML provider activation in a realm. Rotation happens via an admin endpoint (or programmatic API call) that promotes the active cert to "previous" with a 30-day overlap window — SP metadata advertises both during that window so IdPs still validating against the previous key keep working.
- **IdP certs** (theirs). Modgud's metadata-refresh background service re-fetches the IdP metadata every 24 hours (per-provider override: 1h / 6h / 24h / 7d) and updates the cached signing-cert list. Cert-rotation transitions get an audit-log entry.

## Out of scope (v1)

- **Single-Logout (SLO)** — logout in Modgud only clears Modgud's cookie. The customer-IdP session stays alive. SLO is on the roadmap.
- **Modgud as SAML IdP** — Modgud is SP-only in v1. If you have a legacy app that speaks only SAML and you want Modgud to authenticate it, that's a future capability; today the answer is "use a SAML→OIDC bridge" or "use Modgud's OIDC".
- **Encrypted assertions** — supported in the FlavorData config but off by default. Most IdPs only sign; they don't encrypt at the assertion level (TLS is enough).
- **Artifact binding** — HTTP-Redirect and HTTP-POST only. Add on demand.

## Troubleshooting

- **"Cannot enable a SAML provider without IdP metadata"** — set either MetadataUrl or MetadataXml in the Verbindung tab + Speichern, then enable.
- **IdP error: "Unable to locate metadata for ..."** — the IdP doesn't know about our SP EntityID. Re-import our SP metadata at `https://<modgud-host>/saml/<provider-id>/sp-metadata` into the IdP setup.
- **Login redirects to error page** — check the backend log around the time of the failed login. Common causes: clock skew (assertion's NotBefore/NotOnOrAfter outside the validation window), wrong audience (SP EntityID mismatch), or signature validation failure (IdP rotated its key but Modgud's cache is stale — hit "Refresh metadata" or wait for the next scheduled refresh).
- **User signed in but with wrong identity** — check the Verknüpfung & Richtlinien tab. Email-based auto-linking can bind an incoming SAML identity to an existing Modgud user whose email matches. Turn off "Email-basierte Auto-Verknüpfung" if you want SAML logins to JIT-create users instead.
