# frontend-vue — LEGACY (read-only reference)

This is the **legacy** frontend of cocoar.auth. It is kept as a reference while `../frontend-next/` is being built.

## Do not modify

- All new work happens in `../frontend-next/`.
- This folder is a quarry: port specific views (OAuth admin, Realm admin, Setup/Register/Reset/Confirm) into `frontend-next/` as needed.
- Bug fixes here are pointless — the bug only matters if it would re-appear in `frontend-next/`. If so, fix it in `frontend-next/`.

## When this folder will be deleted

Once `frontend-next/` is production-ready and the cutover is complete. At that point this folder is removed and `frontend-next/` is renamed back to `frontend-vue/`.

A pre-cutover snapshot will be tagged in git so the legacy code remains accessible via `git checkout <tag>`.
