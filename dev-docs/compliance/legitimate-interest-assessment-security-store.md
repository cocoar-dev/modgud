---
title: Legitimate-Interest Assessment — streamless security/ops store
description: GDPR Art. 6(1)(f) balancing test for the streamless security/ops audit store (SecurityAuditEntry). Companion to the logging/audit redesign §A.5.
---

# Legitimate-Interest Assessment (LIA) — streamless security/ops store

> **Status:** Drafted 2026-06-03 alongside Phase 3 of the [logging/audit redesign](./../future-features/logging-audit-redesign.md). This is the Art. 6(1)(f) balancing test the design names as a production prerequisite (§A.5). It must be reviewed and signed off by the controller (the deploying operator) before the store processes real end-user data in production; a self-hosting operator adapts the "controller", "retention", and "disclosure" sections to their deployment and privacy policy.

## 1. What is processed, and where

The **streamless security/ops store** (`SecurityAuditEntry`, system DB, cross-realm) records security and operational events that have **no aggregate event stream to attach to** — they are not a registered user's personal data on that user's stream. It is the typed successor to the streamless portion of the retired `"Auth:"` log. Two record families:

- **Security (tenant-relevant):** failed logins against an *unknown/inactive* username, invalid magic-link probes, rejected external/federation logins (allowlist / JIT-disabled / malformed / SAML signature failure), blocked identity-hijack and JIT email-conflict attempts, blocked privilege-escalation, rate-limit hits, DCR registration rejections, bootstrap-invite rejections.
- **Operational (platform-relevant):** signing-key / SAML-certificate rotation, SAML metadata refresh, recovery-CLI invocations, realm provisioning / adoption / control-plane transfer, account-lifecycle sweeps, DCR registration / GC / first-use, bootstrap-invite issuance, and the audit-of-the-audit (log cleared / exported).

**Personal data it may contain:** an *attempted* username or email (an identifier of whoever made the attempt), a source **IP address** (personal data under CJEU *Breyer*, C-582/14), and — for operational actions — the **acting admin's** username. Emails are masked at the call site (`LogPiiMasking.MaskEmail`). It deliberately holds **no** secrets, tokens, invite codes, magic-link URLs, passwords, or request/response bodies.

This LIA covers **only** this streamless store. The per-realm GDPR-audit (`AuthAuditView`) is a registered user's own personal data on their event stream, processed and erased per subject — it is *not* under legitimate interest and is out of scope here.

## 2. Why not per-subject erasure (the boundary)

A record about an **unidentified actor** has no user stream to mask/erase in place. Forcing it into the per-subject erase path is impossible for the unknown-actor case and, for the borderline known-actor case (e.g. a failed attempt whose attempted email later registers), would put an identified subject's security record *outside* the protections of their own erasable stream while pretending otherwise. The design draws the boundary explicitly: stream-backed = erasable in place; **streamless = lawful under legitimate interest, with short retention as the proportionality control** rather than per-subject erasure. This LIA is the documentation of that lawfulness.

## 3. Purpose test — is there a legitimate interest?

**Yes.** The purposes are:

1. **Security / abuse detection.** Detecting and responding to credential-stuffing, password-spray, account-takeover attempts, federation misconfiguration probes, and SAML tampering is a textbook legitimate interest, expressly recognised by Recital 49 GDPR ("ensuring network and information security … constitutes a legitimate interest"). A realm-admin needs to *see* attacks against their realm's login surface to respond (lockout policy, IP blocks, alerting).
2. **Operational forensics / accountability.** A tamper-evident-enough record of privileged operational actions (key rotation, recovery-CLI break-glass, control-plane transfer, who cleared the audit) is necessary for incident response and operator accountability.

The interest is **real and present** (these attacks happen continuously against any public IdP), not speculative.

## 4. Necessity test — is the processing necessary?

**Yes, and minimised.** The interest cannot be met without retaining *some* attacker-attributable signal:

- **Raw IP is necessary, short-term.** Correlating attempts (same IP across many usernames = spray; many IPs against one username = stuffing), feeding manual/automatic IP blocking, and giving a realm-admin actionable "who, from where" all require the **raw** IP, not a hash or geo-only reduction:
  - A **hash** defeats the primary use (you cannot block, allowlist, or range-correlate a salted hash; an unsalted hash is trivially reversible for IPv4 and so offers no real minimisation).
  - **Geo-only** loses the per-host correlation that distinguishes an attack from noise and cannot drive a block.
  - The minimisation applied instead is **time** (§5): the raw IP exists only for a short window, after which it is hard-deleted.
- **Attempted identifier is necessary.** "Failed login for *which* account" is the security signal a realm-admin acts on; without it the row is useless for detecting a targeted takeover. Emails are masked; an attempted bare username is retained as-is (it is not, on its own, more than the attacker chose to type).
- **No excess.** No bodies, tokens, secrets, cookies, or passwords are stored. The store records *occurrences and the minimum attribution*, not payloads.

## 5. Proportionality / balancing test — does it override the data subject's interests?

**On balance, yes, given the safeguards** — the controls below keep the impact on data subjects low and proportionate to the security benefit.

**Retention = the proportionality control.** A **fixed, short hard-prune** (default **7 days**, `SecurityAuditPruneJob`) genuinely deletes rows past the window — this is a real deletion, not a hidden archive. The window is deliberately **not per-realm configurable**, so an operator cannot quietly turn a tight security window into an open-ended dossier. (Contrast: the per-realm GDPR-audit *visibility window* is a view bound over kept-with-the-account history, a different concept — see §A.6 of the design.)

**Access is tightly gated:**

- Read is gated on the `auth-log:read` permission (seeded onto the realm-admin and the User Manager role); no public-network exposure of the raw store.
- **Scope at read:** a tenant realm-admin sees only their **own realm's tenant-visible** rows; control-plane-only operational rows (`PlatformOnly`) are visible to the control-plane operator only. (Carried forward from PR #50's `ScopeToCallerRealm`, extended with the visibility gate.)
- **Clearing/exporting the log is itself audited** (audit-of-the-audit: `audit.log_cleared` / `audit.log_exported` records the operator + realm + timestamp), so destructive or exfiltrating operator actions leave their own trail.

**Reasonable expectations.** A person attempting to log in to an identity provider would reasonably expect that failed/anomalous attempts are logged briefly for security — this is standard practice and aligns with Recital 49. The processing is not used for any secondary purpose (no profiling, no marketing, no automated decisions with legal effect).

**Residual risk is low:** the data is minimal, masked where it is an email, access-controlled, realm-scoped, short-lived, and never enriched or sold.

## 6. Data-subject rights

- **Erasure (Art. 17).** Streamless records are retained under Art. 6(1)(f) with the short retention window as the safeguard; Art. 17(1)(c)/(3) permits retention of a de-identified-by-time security record. They are **not** swept by a registered user's permanent-erase (verified by test: a streamless row survives the user's erase). **Decision #4 (settled): time-expiry only** — Phase 3 does **not** scan-and-purge the store for a newly-registered user's email. Rationale: the short window expires pre-registration rows quickly; a scan-on-registration would itself require matching the new user's email against the store (more processing, more linkage) for marginal benefit. **This must be disclosed in the privacy policy** ("security-relevant login attempts, including from unregistered visitors, are retained for up to N days for abuse detection and are not linked to later accounts").
- **Access / objection (Art. 15 / 21).** Pre-registration attempt records are treated as **time-expiring security records**, not as a per-individual file surfaced on an Art-15 request — there is no reliable, privacy-preserving way to authenticate that a requester "owns" an arbitrary attempted identifier/IP, and surfacing them would create a lookup oracle. The controller discloses this stance in the privacy policy. (If a controller chooses to honour such requests, identity verification and the resulting linkage must be assessed separately.)
- **Operational records about admins** (who rotated a key, ran the recovery CLI, cleared the log) are processed for accountability; the acting admin is an operator, not a consumer data subject, and the same short retention applies.

## 7. Safeguards summary

| Safeguard | Mechanism |
|---|---|
| Data minimisation | typed fields only; no secrets/tokens/bodies; emails masked (`LogPiiMasking.MaskEmail`) |
| Storage limitation | fixed short hard-prune (`SecurityAuditPruneJob`, default 7 days), not per-realm configurable |
| Access control | `auth-log:read`; realm + `PlatformOnly` scope at read; no public exposure |
| Accountability | audit-of-the-audit (`audit.log_cleared` / `audit.log_exported`) |
| Purpose limitation | security + operational forensics only; no profiling / secondary use |
| Boundary integrity | identified users' auth events stay on their erasable stream (the GDPR-audit), not here |

## 8. Outcome

Subject to controller sign-off and privacy-policy disclosure (§6), the processing in the streamless security/ops store is assessed as **lawful under Art. 6(1)(f)**: a real security interest, necessary and minimised, with the short fixed retention window plus access-gating and audit-of-the-audit keeping the impact on data subjects proportionate.

**Controller actions before production:** (1) set/confirm the retention window and state it in the privacy policy; (2) disclose the unregistered-visitor logging + the time-expiry-only stance (§6); (3) record the sign-off (owner + date) below.

> _Sign-off: ____________________ (controller) — date: ___________
