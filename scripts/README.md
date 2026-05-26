# scripts/

Operator scripts that drive Modgud as a regular API client. They have
the same trust boundary as any other admin user — none of them write to the
DB directly. If a script can do something, the API can do it; if the API
can't, neither can a script.

## seed-demo.mjs

Seeds `data/demo-seed.json` into a fresh deployment via the admin API.

```bash
# Defaults: localhost:9099, admin / ABC12abc!, system realm
node scripts/seed-demo.mjs

# Custom target / credentials
node scripts/seed-demo.mjs \
    --base-url=http://localhost:9099 \
    --user=admin \
    --password='ABC12abc!' \
    --realm=acme
```

Prerequisites:
1. The API is running.
2. The realm has an admin (bootstrap via `dotnet Modgud.Api.dll recover
   bootstrap-admin --email …` if it doesn't).
3. The admin can log in.

Idempotent: every entity (roles, users, groups, scopes, APIs, clients,
login providers) is checked by its natural key and skipped if it already
exists. Re-running the script after a partial run only creates what's
missing.

The script prints generated client/API secrets at the end. Those values
are not retrievable from the API later — capture them from stdout, or
rotate via `POST /api/admin/oauth/clients/{id}/secret`.

### Why a script and not a backend service

An earlier iteration shipped an in-process `IDemoSeedService` that wrote
to the database directly,
bypassing the admin API. That meant:
- A second write path that could drift from the API (validation, events,
  permission checks).
- A non-Production-only DI registration that was easy to miss in code review.
- The seed file shipped in the container image was treated as a security
  hazard (PROD-01).

By making the seeder a script that POSTs through the regular API, the
demo-seed itself doubles as an end-to-end smoke test — every demo run
exercises the same code path that real admin UI clicks do.
