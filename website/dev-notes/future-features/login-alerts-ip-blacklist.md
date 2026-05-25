# Login alerts + manual IP blacklist

> **Status:** Idea captured 2026-05-07. Not started.
> **Why:** We explicitly rejected automated IP rate-limiting on
> `/api/account/login` because of the NAT-aussperr-Risk (one user's
> typos would lock out a whole corporate network behind a NAT). But
> brute-force should not go unnoticed. The compromise: detect
> anomalies, alert the admin, let the human decide whether to block.

## The pattern

This is the NAT-safe industry-standard approach — same shape used by
AWS Cognito, Auth0, Okta. **Observation is automatic, blocking is
human.**

Two components:

1. **Alert system** — when failed-login volume from an IP exceeds a
   configurable threshold within a window, fire an alert (email +
   in-app SignalR push to active admin sessions).
2. **Manual app-level IP blacklist** — admin decides on the basis of
   the alert whether to block. A middleware enforces the blocklist
   on auth-bearing paths. Block returns 403 (not 401 — must not look
   like wrong-credentials).

## Why human-decided

- **NAT scenario:** corporate network of 500 users behind one IP.
  One user fat-fingers the password 5 times → naïve auto-block
  locks out 499 innocent colleagues. Service outage masquerading
  as security feature.
- **Mobile carrier CGNAT:** half a country can share the same egress
  IP for some carriers. Auto-block is even worse.
- **Tor exit nodes:** legitimate privacy users get blocked
  collectively; many SaaS apps want to allow them with extra
  friction (CAPTCHA), not outright block.

A human reviewing an alert can distinguish "1000 attempts/min from
this IP, none successful" (real attack) from "30 attempts in 10 min,
2 successful, all the same domain" (legit user with a sticky old
password) and decide accordingly.

## Detection design

### What to count

- **Failed login** = 401 from `/api/account/login`,
  `/api/account/mfa/login`, `/api/account/email-otp/login`.
- Counted per source IP, in a rolling time window (e.g. 15 min).

### Where the count lives

- In-memory counter per IP (cheap, lost on restart) is probably
  fine for v1.
- Persisted aggregate in Marten if we want to survive restarts and
  cross-instance state (only matters if we ever scale horizontally,
  which we don't currently).
- **Don't use Redis just for this** — pulls in an unnecessary
  dependency.

### Trigger threshold

- Configurable, default e.g. **20 failures / 15 min / IP**.
- After firing once, **cooldown** for the same IP (e.g. 1 hour) so
  the admin isn't spammed with re-triggers from the same attack.

### Alert delivery

- **Email** to all admins with `realm:admin` permission (uses the
  existing email pipeline).
- **SignalR push** to active admin sessions (uses
  `@cocoar/signalarrr` infrastructure already in place — admins
  see a toast / notification in the running admin UI).
- Both should include: IP, time window, failure count, sample of
  attempted usernames, "block this IP" link straight to the
  admin's IP-blacklist UI.

## Blacklist enforcement

### Storage

- Tenant-scoped (per realm) **and** deployment-global (in `system`
  realm).
- Tenant admin can blacklist for their own realm.
- Control-plane admin can blacklist deployment-wide.
- Schema sketch: `IpBlacklistEntry` document with `RealmId?`
  (null = global), `IpOrCidr`, `Reason`, `ExpiresAt?`, `CreatedBy`,
  `CreatedAt`.

### Middleware placement

- Run **early** in the pipeline — before `RealmMiddleware`? Maybe.
  Definitely before authentication.
- Match request IP against:
  - global blacklist (always)
  - the resolved-tenant's blacklist (if `RealmMiddleware` already
    ran)
- Match wins → return **403** (not 401, must not look like
  wrong-credentials).

### Scope of paths

- Block on auth paths (`/api/account/login`, `/api/account/mfa/*`,
  `/connect/authorize`).
- Don't block on the SPA shell (`/`, `/login`, static assets) —
  blocked IP should still see "you are blocked" page, not a
  connection refusal.
- Special endpoint that always responds even when blacklisted:
  `/api/account/blocked-info` returning a polite "if this is wrong,
  contact your admin" page.

## Features that fall out for free

Once the blacklist storage exists, several adjacent features become
trivial:

- **Allowlist** (inverse) — only allow specified IP ranges to reach
  the auth surface. Useful for internal-only deployments.
- **CIDR support** — once we parse CIDR for blacklist matching,
  scale-up to country-block (with MaxMind data) and ASN-block is
  also doable. Not v1 features but hooks remain open.
- **TTL-based blocks** — "block for 24h", "block for 7 days",
  "permanent". Real operators want this; permanent-only would mean
  the list grows forever.
- **Audit log** — every add/remove gets an event in the admin audit
  log. Forensic gold dust when an admin later wants to know "why
  am I blocked".

## Architecture: in modgud or in alert-hub?

We have a sibling project `alert-hub` (per the claude-memory
reference) that does generic alerting infrastructure. Two options:

| Option | Where the alerting lives |
|---|---|
| **A** — In modgud | Self-contained, no external dependency. Simpler MVP. |
| **B** — Delegated to alert-hub | Reusable across all cocoar SaaS projects, but adds an integration boundary. |

**Recommendation:** Option A for v1 (alert pipeline lives in
modgud), with the events emitted in a format that alert-hub
could consume later if/when we want to centralise.

## Effort estimate

- Counter + threshold + alert pipeline: ~2 days
- Email + SignalR notifications: ~1 day (mostly template work,
  infrastructure exists)
- Blacklist storage + middleware: ~1.5 days
- Admin UI (alert feed + blacklist CRUD with TTL + CIDR): ~3 days
- Audit log integration: ~0.5 days
- **Total:** ~1.5 weeks for full feature

## What this is NOT

- **Not auto-blocking.** Always human-in-the-loop.
- **Not a fail2ban replacement.** fail2ban is system-level (firewall
  rules); this is application-level (HTTP middleware). They can
  coexist for layered defence.
- **Not a CAPTCHA replacement.** CAPTCHA is the friction-based
  alternative — slow attackers without blocking. A future
  complementary feature.

## Trigger events

This stays an idea until one of:
- A real brute-force incident happens against a customer realm
  and the admin asks "why didn't I see this coming?"
- An enterprise customer's onboarding checklist requires "anomaly
  detection on auth endpoints"
- A second feature emerges that needs the same alert pipeline
  (e.g. "alert me when someone enables 2FA-exempt for a user")
  and it makes sense to build the alert spine once
