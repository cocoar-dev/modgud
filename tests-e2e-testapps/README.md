# tests-e2e-testapps

Playwright E2E suite for the Cocoar.Auth test apps:
`src/dotnet/TestApps/Cocoar.Auth.TestApps.ResourceApi`,
`src/dotnet/TestApps/Cocoar.Auth.TestApps.Bff`, and the
`src/test-clients-vue/test-spa-bff` Vue SPA.

The suite drives **real OAuth/OIDC flows** against a running Cocoar.Auth
instance — it never mocks the IdP.

## What it covers

- **`01-resource-api.spec.ts`** — direct contract: 401/403 on the resource
  API, client-credentials token round-trip, IdP discovery doc.
- **`02-bff-anonymous.spec.ts`** — BFF without a session: 401s, CSRF
  guard (`X-Requested-With`), `/bff/login` redirect to the IdP.
- **`03-bff-login-flow.spec.ts`** — full browser flow: login form →
  OIDC callback → cookie session → `/bff/user` → `/api/me` proxied
  through with server-side bearer token → logout.

## Pre-conditions

1. **Cocoar.Auth running** at `http://localhost:9099` (override with
   `TESTAPPS_AUTHORITY`).
2. **Demo seed loaded** — run the `/setup` wizard and tick "load demo
   data", or POST `LoadDemoData=true`. The seed provides:
   - `demo-bff` confidential client (the BFF uses it).
   - `demo-backend` client-credentials client (ConfidentialClient + spec 01).
   - `demo-api` resource server with `demo.read` / `demo.write` / `demo.admin`.
   - `demo.admin` user / password `Demo1234!`.
3. **.NET 10 SDK** on PATH — Playwright's `webServer` starts the
   ResourceApi (port 7081) and the BFF (port 7080) for you.

## Running

```bash
pnpm install
pnpm exec playwright install chromium
pnpm test
```

Override defaults via env:

```bash
TESTAPPS_AUTHORITY=http://localhost:14200 pnpm test  # against the integration rig
```

## Notes

- `webServer` reuses already-running processes locally
  (`reuseExistingServer: !process.env.CI`), so iterating on a single
  spec doesn't pay the dotnet-startup cost every time.
- The suite never seeds or resets the auth DB. If a test fails because
  `demo.admin` is missing, run the demo-seed import manually in the
  setup wizard before re-running.
- Specs run sequentially (`workers: 1`) because `demo.admin` is
  shared and the cookie state isn't worth parallelising.
