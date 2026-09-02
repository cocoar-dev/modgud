# Configuration Drafts

Realm configuration in Modgud is **staged like code**. For a realm admin, every save in the admin UI is a *commit* onto a **draft**, and nothing touches the live realm until the draft is **applied** — in one transaction, all or nothing. If you know git, you already know the model:

| git | Modgud |
|---|---|
| `main` | The live realm configuration |
| A branch | A **draft** (server-side, stored in the realm) |
| A commit | Saving any admin modal — or deleting an entity from a list |
| Push + merge | **Apply draft** |
| The merge base | The draft's **baseline** — a snapshot of the realm taken when the draft was created |
| A merge conflict | A three-way **conflict**: the live realm changed while your draft was open |
| Rebase | *Confirm remaining differences* — the baseline moves to the current live state |
| Switching branches | **Park** a draft / switch to another one |

There is nothing to set up and no mode to enter: staging engages automatically for admins holding `realm:admin`. Admins with only resource-scoped permissions (e.g. `user:write`) keep the classic behavior — their saves apply immediately.

## Day to day

Open any entity in its normal modal — a user, an OAuth client, a role, the realm settings — change something and save. The footer button reads **Stage to draft** instead of Save, and the first staged change implicitly creates an **auto-named draft** (your name + timestamp). You never create a draft up front.

While a draft is checked out, a **staging bar** sits at the bottom of the admin area: the draft's name, how many changes are staged, plus **Review**, **Park** and **Apply**. Lists show the *merged* state — staged edits overlay their live rows, entities created in the draft appear as `Staged (new)` rows, and staged deletions mark their row in red.

- **Apply now or keep going** — apply after one change (two clicks), or stage ten changes across five entity types and apply them together. Apply runs in a **single database transaction**: either the whole draft lands, or nothing does. Consequence actions (token revocations triggered by a change) run only after the transaction commits.
- **Quick fix while a draft is open** — park the current draft, make the urgent change (this starts a fresh draft), apply it, then switch back to the parked draft. Exactly like stashing on one branch to hotfix on another.
- **Multiple drafts** — you can have any number of parked drafts; the [Configuration Drafts page](#the-configuration-drafts-page) is the branch overview for switching between them.

::: tip Generated client secrets
A confidential OAuth client created through a draft gets its generated secret **returned once, at apply**. Copy it from the apply result — it cannot be read back later.
:::

## Deletes are staged too

Deleting an entity from a list is a commit like any other: the row turns red (`Staged (delete)`), the apply removes the entity through the same delete operation the live admin API uses, in dependency order, inside the same transaction. Deleting the row again (or the **Undo delete** context action) takes the deletion back out of the draft.

Two special cases worth knowing:

- **Users** — applying a staged user deletion moves the user into the **recycle bin** exactly as a live delete would: deactivated, scheduled for deletion, restorable during the grace period. The bin's restore and permanent-erase operations stay live actions.
- **Protected targets** — the lockout and infrastructure protections that guard [prune](realm-provisioning#apply-merge-vs-prune) apply here too: the system app, auto-seeded standard scopes, service-account-linked and terminal-managed clients, the built-in Internal login provider, and anything conferring `realm:admin` (a realm-admin role, any current admin user, an admin-conferring group). Staging the deletion of a protected target flags a **plan error** and blocks the apply until you unstage it.

## What stays immediate

Drafts stage **configuration**. Operational **actions** — anything with its own lifecycle, audit identity or urgency — act immediately, as they always did:

| Immediate | Why |
|---|---|
| Deactivating a user, the client **Disable (immediate)** grid action, the login-provider grid toggle | Emergency levers — "this must stop working *now*" should never wait for an apply |
| Session revocation, force-locking staffing sessions, 2FA resets, admin password set, magic links | Security actions, not state |
| Secret rotation (client secrets, provider secrets) | Credential material with its own audit trail |
| Recycle-bin restore and permanent erase | Lifecycle operations on the bin |
| Service accounts and their credentials, terminal slots, position grants/activation tokens | Credential material the manifest deliberately does not model |

The same distinction shows up inside modals: for example the client modal's *Enabled* checkbox stages with the rest of the form, while the grid's *Disable (immediate)* action is the live kill switch.

## Conflicts — when live moves under your draft

Every draft remembers the realm state it started from (its baseline). When the plan detects that the **live realm changed while your draft was open**, it raises a conflict instead of silently overwriting:

- **Stale overwrite** — someone changed a field live; your draft still carries the old value and applying would revert their change without you noticing. This is the case the baseline exists for.
- **Both changed** — the field (or, for a staged deletion, the entity) changed live *and* in your draft — git's edit/edit and modify/delete conflicts.
- **Created / deleted live** — an entity your draft touches was created or removed live in the meantime.

The apply is **refused while conflicts are open**. Resolve them per field with *Take live value* in the entity's review card, or use *Confirm remaining differences* (rebase) to declare everything still differing as intentional.

Drafts are **private by default**; share one to let every realm admin see, edit and apply it (one admin scaffolds the structure, another adds their client). Concurrent edits are protected by optimistic versioning — a save against a stale draft version is rejected and reloaded rather than lost.

## Secrets in drafts

Secret-bearing fields — user passwords, client secrets, login-provider secrets, the captcha secret — are **write-only** in a draft: the value is encrypted at rest, the UI only ever shows *that* a secret is staged, and exports never contain it. At apply the staged secret is merged back in memory and set through the normal operation.

## The Configuration Drafts page

*System → Configuration Drafts* is the branch overview and review surface:

- **Your drafts** (and drafts shared with you): open, park, switch between, or discard them.
- **Review** — the exact change plan per entity: creates, updates with per-field before/after, deletions, notes and conflicts. By default only actual changes are shown; unchanged entries can be revealed.
- **Edit in place** — add entities directly to a draft, or open any entry as JSON for surgical edits.
- **Start a draft from a manifest** — upload a JSON manifest (hand-written or machine-generated) as a new draft, review its plan, then apply. This is the interactive import path.
- **Export** the current realm configuration and download the **manifest JSON Schema**.
- **Prune** — opt-in full sync: the apply additionally deletes entities absent from the draft, with the same protections as [declarative provisioning](realm-provisioning#apply-merge-vs-prune).

## Drafts are manifests

A draft's content *is* a [declarative provisioning manifest](realm-provisioning) — the same schema, the same apply engine, the same guarantees. That makes the draft workspace the **human review gate** in front of automation: an agent (or a colleague) authors a manifest against the published schema, you load it as a draft, read the plan, resolve anything unexpected, and apply. Conversely, everything you stage through the UI can be exported as a manifest and re-applied elsewhere.

## Current limits

- **Clearing** a stored optional value (a client token lifetime, an email-domain allowlist) cannot be expressed by the manifest — omitted means *unchanged*. Use the admin API to clear such a field.
- **Renaming** a group or position stages a *new* entity under the new name (they have no stable key separate from the name); users and roles rename cleanly.
- **App permission-catalog renames** keep their id-stable semantics only through a live save — the app modal automatically falls back to an immediate save when it detects a catalog rename.
- Entities the manifest does not model (service accounts, terminal slots, SA-linked and terminal-managed clients) are managed live in their own admin surfaces.
