# Key material

Modgud signs, encrypts, and stores several distinct kinds of key
material, spread across two very different places: some of it lives
in Postgres alongside the rest of a realm's data, some of it lives on
disk as `.pfx` files. That split matters operationally — the two
halves have different backup requirements, different rotation
mechanics, and different blast radii when something goes wrong. This
page is the map: what each key protects, where it physically lives,
how it rotates, and what breaks if it's lost.

## At a glance

| Key material | Protects | Lives in | Rotation | Loss |
|---|---|---|---|---|
| Per-realm RSA signing key | Access & ID tokens (that realm only) | That realm's tenant DB | Manual, 30-day overlap | Fresh key auto-bootstraps; that realm's outstanding access/ID tokens die |
| OpenIddict signing certificate (`signing.pfx`) | Authorization codes, device codes, refresh tokens (all realms) | Disk volume `data/keys/` | Manual file swap, overlap via `PreviousSigningCertificatePaths` | Auto-regenerates; **every** realm's outstanding codes and refresh tokens fail at once |
| OpenIddict encryption certificate (`encryption.pfx`) | Same artifacts, wrapped as JWE (access tokens excluded) | Disk volume `data/keys/` (falls back to the signing cert if unset) | Manual file swap, **no overlap today** | Same as above — instantly, with no grace window |
| Per-tenant DataProtection key ring | Auth cookies, antiforgery tokens, stored login-provider client secrets | That realm's tenant DB | Automatic, ~90-day framework default | Silent logout + that realm's stored provider secrets become undecryptable |
| WebAuthn passkey key pairs | Passkey login ceremonies | That realm's tenant DB (public key only — the private key never leaves the authenticator) | None — re-enrolment only | Affected user re-enrols a passkey |

The rest of this page walks through each row.

## Per-realm RSA signing key

Every realm has its own RSA keypair, generated on first boot and
stored as a `RealmSigningKey` document in that realm's own tenant
database. This key signs **access tokens and ID tokens** — the tokens
that leave the IdP and cross the trust boundary to a resource server
or client application.

It's also the only key material published in that realm's
`/.well-known/openid-configuration` JWKS document. Each realm's JWKS
contains exclusively its own key(s), never another realm's — which is
what makes the isolation structural rather than a matter of
convention: a resource server validating against realm B's JWKS has no
way to accept a token signed by realm A's key, because that key was
never in scope. Rotating one realm's signing key has zero effect on
any other realm.

Rotation is manual — there's no scheduled auto-rotation. Trigger it
either from **Realm Settings** in the admin UI or with the Recovery
CLI:

```bash
dotnet Modgud.Api.dll recover rotate-signing-key --realm acme
```

A rotation generates a fresh keypair and immediately makes it the
active signing key, but the **previous** key isn't deleted — it's
retired and kept in the JWKS for a 30-day verification-overlap window,
so access/ID tokens issued just before the rotation stay validatable
until they naturally expire. A daily janitor job hard-deletes retired
keys once their overlap window has elapsed, so retired private key
material doesn't accumulate indefinitely in the tenant DB.

Because the key lives in Postgres, it restores exactly the way the
rest of a realm's data does: restore the tenant DB dump and the key
(active and any still-in-overlap retired keys) comes back with it. If
the tenant DB is lost outright and rebuilt from scratch instead of
restored, a fresh key bootstraps automatically — but every access and
ID token outstanding for that realm at the time is now unverifiable
and the affected users simply have to sign in again.

## Global OpenIddict signing certificate

Separately from the per-realm keys above, there is exactly **one**
OpenIddict signing certificate per instance (`signing.pfx`, on disk
under `data/keys/`, auto-generated on first boot if missing — see
[Docker & Deployment](./deployment#minimum-env-vars)). It's shared by
every realm on that instance.

This certificate signs only artifacts that are redeemed **at the IdP
itself**: authorization codes, device codes, and refresh tokens. None
of those are meant to be validated by a third party — a client only
ever holds an opaque reference id and hands it back to Modgud's own
`/connect/token` or `/connect/revoke` endpoints. That's a deliberate
design choice: giving each realm its own certificate here would add
validation surface without buying any additional isolation, since
these artifacts never leave the IdP as bearer material in the first
place. Consequently this certificate is **never** published in any
realm's JWKS.

Rotation is a manual file swap. To roll it without invalidating
everything in flight, place the new file and list the old one in
`OpenIddict.PreviousSigningCertificatePaths` so both are trusted during
the transition, then drop the old path once you're confident nothing
still depends on it.

Because this certificate is shared instance-wide, losing it (or
letting a container restart regenerate it because the `data/keys/`
volume wasn't persisted) has an instance-wide effect: a fresh
self-signed certificate auto-generates, and **every realm's**
outstanding authorization codes and refresh tokens fail simultaneously
— affected users re-authenticate, in-flight authorization-code
exchanges fail. Access and ID tokens are unaffected, since those are
signed by the per-realm key above, not this one.

## Global OpenIddict encryption certificate

OpenIddict also wraps the same IdP-internal artifacts (authorization
codes, device codes, refresh tokens) as JWE using a second
certificate, `encryption.pfx` — again one per instance, again on disk
under `data/keys/`, falling back to the signing certificate if
`OpenIddict.EncryptionCertificatePath` is left unset. **Access tokens
are deliberately excluded** — they stay signed-only (JWS, no
encryption layer) precisely so that any resource server capable of
validating a JWKS-published signature can verify them locally, without
needing this certificate at all.

::: danger No rotation overlap today
Unlike the signing certificate, there is currently **no equivalent
overlap mechanism** for the encryption certificate. Swapping the file
takes effect immediately and instance-wide: every live authorization
code and refresh token across every realm stops decrypting the moment
the new certificate is loaded, with no grace period. Treat replacing
`encryption.pfx` as a maintenance window, not a hot-swappable
operation — plan for the affected users to need to sign in again.
Tracked for a proper overlap mechanism:
[#125](https://github.com/cocoar-dev/modgud/issues/125).
:::

## Per-tenant DataProtection key ring

Each realm also has its own ASP.NET Core DataProtection key ring,
stored as `DataProtectionKeyDocument` documents in that realm's tenant
database (see [Persistence (Marten)](./database)). This ring protects
the first-party auth cookie, antiforgery tokens, and any stored
login-provider client secrets (OIDC/SAML federation config) for that
realm.

It rotates automatically on the ASP.NET Core framework's own schedule
(a new key roughly every 90 days by default) — there's no manual step
and nothing to remember here day to day. Because the ring lives in
Postgres, it restores with the tenant DB dump the same way everything
else in this list does; losing the tenant DB without a restore means a
fresh ring is generated, every existing session cookie for that realm
silently stops validating (affected users are logged out, not shown an
error), and any stored login-provider secrets encrypted under the old
ring become permanently undecryptable.

There's an optional extra layer on top: set
`DataProtection__CertificatePath` (and optionally
`DataProtection__CertificatePassword`) to an operator-supplied
certificate, and every realm's key ring gets wrapped at rest with it —
so a raw tenant-DB dump exposes ciphertext instead of usable key
material. This is genuinely instance-wide: if you configure it and
then lose *that* certificate, **no** realm's ring can be unwrapped
anymore, which is a much bigger outage than any single tenant DB
issue. If you leave it unset, the per-database boundary between realms
is the protection instead, and there's no extra certificate to lose.

## WebAuthn passkey key pairs

Passkey (WebAuthn) credentials work the other way around from
everything above: Modgud only ever stores the **public** key half, per
user and per credential, in that realm's tenant database. The private
key is generated and held by the user's authenticator (a hardware key,
a platform authenticator, a password manager) and never transmitted to
or stored by Modgud at all — the server's job is limited to verifying
signatures against the stored public key during login.

There's no rotation concept for a passkey — a credential is valid
until the user removes it or the authenticator is lost, and recovery
is always re-enrolment rather than any kind of key recovery.

## Cross-realm isolation, honestly

Tokens that actually leave the IdP and get handed to a resource server
or client — access tokens and ID tokens — are cryptographically
realm-separated: each realm has its own signing key, published only in
that realm's own JWKS, so a token from one realm is structurally
incapable of validating against another realm's discovery document.

Everything that stays inside the IdP — authorization codes, device
codes, and refresh tokens, plus their JWE wrapping — shares one
certificate pair for the whole instance, by design, because those
artifacts are never handed to a resource server as bearer material in
the first place. The trade-off is honest: a compromise of that
certificate pair affects every realm's in-flight internal artifacts at
once, not just one tenant's. In practice that means the `data/keys/`
PFX volume deserves the same operational care — backup, access
control, monitoring — as the Postgres databases themselves, even
though it's "just two files."

## See also

- [Backing up realms](./database#backing-up-realms) — how the
  DB-persisted keys ride along with the regular per-tenant `pg_dump`
  backups, and why the PFX volume needs a separate backup step of its
  own.
- [Recovery CLI: `rotate-signing-key`](./recovery-cli#rotate-signing-key)
  — the command-line path to rotating a realm's signing key.
- [Sessions & Tokens](../concepts/tokens) — the token formats
  (reference vs. JWT), lifetimes, and revocation paths that these keys
  underpin.
