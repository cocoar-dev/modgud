# Local SAML test IdP

Lightweight SAML 2.0 IdP for testing Modgud's SAML SP federation
slice without needing a real EntraID / ADFS / Okta setup. Based on
[kristophjunge/test-saml-idp](https://github.com/kristophjunge/docker-test-saml-idp),
which wraps [simpleSAMLphp](https://simplesamlphp.org/) in a single
Docker container with pre-configured test users.

## Start

```bash
cd dev/saml-idp
docker compose up -d
```

## Verify

```bash
curl -s http://localhost:8080/simplesaml/saml2/idp/metadata.php | head -20
```

You should see an `<EntityDescriptor>` XML document with the IdP's
EntityID + a `<X509Certificate>` block.

The IdP's admin UI is at <http://localhost:8080/simplesaml/>
(admin / secret) — useful for inspecting the in-IdP session, browsing
the metadata catalogue, and seeing which SP it thinks it's talking to.

## Test users

Baked into the image, no override:

| Username | Password    | mail                | uid    |
| -------- | ----------- | ------------------- | ------ |
| `user1`  | `user1pass` | `user1@example.com` | `user1` |
| `user2`  | `user2pass` | `user2@example.com` | `user2` |

## Wiring it to a Modgud LoginProvider

The compose file ships with placeholder ACS / SLO URLs because
Modgud's per-LoginProvider GUID isn't known until a provider record
exists. The flow:

1. Start the IdP: `docker compose up -d`.
2. In Modgud admin UI, create a SAML provider (any flavor — Generic
   SAML is the simplest for the IdP since it has no preset claim
   URIs). Modgud assigns a GUID — note it, e.g.
   `bb98f59c-37e6-48d0-87ad-cc6fa661535b`.
3. Edit `docker-compose.yml` and replace `devtest` in the
   `SIMPLESAMLPHP_SP_ASSERTION_CONSUMER_SERVICE` and
   `SIMPLESAMLPHP_SP_SINGLE_LOGOUT_SERVICE` env vars with the GUID.
4. `docker compose up -d --force-recreate`.
5. In Modgud, set the provider's IdP Metadata URL to
   `http://localhost:8080/simplesaml/saml2/idp/metadata.php` and
   save. Modgud's metadata-refresh job picks up the signing cert
   and EntityID.
6. Login as `user1` / `user1pass`.

## What this IdP does and doesn't

**Does:**
- HTTP-POST and HTTP-Redirect bindings (the two we support).
- Signed Responses + signed Assertions (the secure-default mode
  Modgud's SAML config requires).
- Standard claim names — `eduPersonPrincipalName`, `mail`, `cn`,
  `sn`, `uid` — matching what a Generic-SAML-flavor in Modgud
  expects without overriding the AttributeMap.

**Doesn't:**
- Encrypted assertions — fine, Modgud's default is encryption off.
- Real vendor-specific claim URIs (Microsoft's
  `http://schemas.xmlsoap.org/...`, ADFS's UPN/WindowsAccountName).
  Use the **Generic SAML** flavor in Modgud when testing against
  this IdP, not the EntraID-SAML or ADFS-SAML preset (those
  pre-fill claim URIs that this IdP doesn't emit).

## When to use what

| Goal                                     | Use                                |
| ---------------------------------------- | ---------------------------------- |
| Verify SAML SP code path basically works | This local IdP                     |
| Test EntraID-specific claim URIs / MFA   | Real EntraID Enterprise App + ngrok |
| Test ADFS quirks                         | Real ADFS or detailed test fixtures |
| Vendor-neutral conformance check         | <https://samltest.id>              |

## Stop / Clean up

```bash
docker compose down
```

Container state is ephemeral — no volumes, restart loses nothing
permanent.
