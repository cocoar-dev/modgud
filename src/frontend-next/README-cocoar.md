# frontend-next — Active Frontend

This is the **active** frontend of cocoar.auth.

It is being built as a fresh codebase based on TimeToDo's frontend shell, extended with the IdP-specific views ported from `../frontend-vue/`:

- OAuth admin (Clients, Scopes, APIs, Login Providers)
- Realm admin
- Setup / Register / Reset / Confirm flows
- Granular sidebar gating

## Source policy

- All new work happens here.
- `../frontend-vue/` is **read-only legacy** — port views from there, do not modify it.
- Once `frontend-next/` is production-ready, `../frontend-vue/` will be deleted and this folder renamed back to `frontend-vue/`.

## Build & run

(Will be filled in once the project skeleton exists.)
