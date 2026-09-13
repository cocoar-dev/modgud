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
- **A save can land on a different entity than the one you opened.** A user's *Direct groups* tab stages the affected **groups**, not the user: a membership is a fact about the group's member list, so the plan shows it under *Groups* as a `Members` change. Reopening the user shows the staged memberships.

::: tip Generated client secrets
A confidential OAuth client created through a draft gets its generated secret **returned once, at apply**. Copy it from the apply result — it cannot be read back later.
:::

## Deletes are staged too

Deleting an entity from a list is a commit like any other: the row turns red (`Staged (delete)`), the apply removes the entity through the same delete operation the live admin API uses, in dependency order, inside the same transaction. Deleting the row again (or the **Undo delete** context action) takes the deletion back out of the draft.

Two special cases worth knowing:

- **Users** — applying a staged user deletion moves the user into the **recycle bin** exactly as a live delete would: deactivated, scheduled for deletion, restorable during the grace period. The bin's restore and permanent-erase operations stay live actions.
- **Protected targets** — the lockout and infrastructure protections that guard [prune](realm-provisioning#apply-merge-vs-prune) apply here too: the system app, auto-seeded standard scopes, terminal-managed clients, the built-in Internal login provider, service accounts themselves, and anything conferring `realm:admin` (a realm-admin role, any current admin user, an admin-conferring group). Staging the deletion of a protected target flags a **plan error** and blocks the apply until you unstage it.

## What stays immediate

Drafts stage **configuration**. Operational **actions** — anything with its own lifecycle, audit identity or urgency — act immediately, as they always did:

| Immediate | Why |
|---|---|
| The client **Disable (immediate)** grid action, the login-provider grid toggle | Emergency levers — "this must stop working *now*" should never wait for an apply |
| Session revocation, force-locking staffing sessions, 2FA resets, admin password set, magic links | Security actions, not state |
| Secret rotation (client secrets, provider secrets, service-account credentials) | Credential material with its own audit trail |
| Recycle-bin restore and permanent erase | Lifecycle operations on the bin |
| Service-account deletion, terminal enrollment and a slot's disable / reactivate / revoke, suspending / resuming a position grant, activation tokens | Ceremonies with a second party or a state machine of their own — the manifest's `Grants` list says *who* is authorized and `Terminals` what a slot looks like; enrolling, suspending and revoking flip a state of their own |
| Resetting or force-expiring a user's 2FA grace clock | Actions on a running clock; the *policy* (grace-period override, exemption) is configuration and stages with the user |
| A scheduled job's **Run now**, and saving a deployment-wide *system* job on the control plane | Firing a job is an action; a system job is not realm configuration (a realm job's schedule, enabled flag and parameters stage) |

The same distinction shows up inside modals: for example the client modal's *Enabled* checkbox stages with the rest of the form, while the grid's *Disable (immediate)* action is the live kill switch. Everything on the user modal stages — profile, active flag (applying it runs the same revocation cascade a live deactivation does, after the transaction commits), email-verified override, the per-user 2FA policy and the direct groups; only the two grace-clock buttons act at once. A user created in a draft can already be put into groups: the draft knows it by a `#handle` until the apply assigns its id.

The same goes for the surfaces that used to be the exceptions: **Branding** (realm logo, colours, product name, mail sender) stages into the draft's settings entity next to the Realm Settings tabs; a **service account** stages with its credentials — adding one stages a credential that is issued at apply with its secret shown once in the apply result, removing one stages its deletion (a red *Clients* entry in the plan), only *Rotate* acts at once; a **position's authorized users and terminal slots** stage on creates, draft rows and edits alike — adding a slot or changing the positions it serves goes onto the draft, and the slot's client is created at apply (its secret, if the binding has one, shown once in the apply result); the device still enrolls afterwards, and suspend/resume of a grant and disable/reactivate/revoke of a slot stay immediate; the API modal's *Create implicit scope* stages the scope. A client_credentials client that is not linked to a service account is an ordinary client and stages like one.

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
- **Selective export** — the cart: pick individual entities (an app, its clients, a group, …) and download a *partial* manifest. Whatever the selection references — transitively — is pulled in automatically and marked *Required*, so the file always applies cleanly; a "Select related" shortcut on an app grabs its clients, APIs, scopes and roles in one click. User references (group members, position grants) are excluded by default, and references that aren't part of the export (standard scopes, the system app) are assumed to exist on the target. See [moving config between realms](#moving-config-between-realms-and-instances).

On a large realm you don't pick entities in a dialog — you collect them where the search and filters are: every admin grid's context menu offers **"Add to export selection"**. The collected entities appear in a footer bar (it survives navigation and reloads, per browser), and **Export…** on that bar opens the selective-export review pre-filled with your collection — showing only the selection plus its required references, with the full list one checkbox away.
- **Prune** — opt-in full sync: the apply additionally deletes entities absent from the draft, with the same protections as [declarative provisioning](realm-provisioning#apply-merge-vs-prune).
- **Pending applies from the API** — a script's `apply?prune=true` that would delete something does not run; it is [parked here as a shared draft](realm-provisioning#a-pruning-apply-asks-first) named *Pending apply (prune) — {time}, by {caller}*, and the script gets the link. Open it, read the plan — deletions in red — and apply or discard. Such a draft prunes however it is applied; that is what the caller asked for.

## Drafts are manifests

A draft's content *is* a [declarative provisioning manifest](realm-provisioning) — the same schema, the same apply engine, the same guarantees. That makes the draft workspace the **human review gate** in front of automation: an agent (or a colleague) authors a manifest against the published schema, you load it as a draft, read the plan, resolve anything unexpected, and apply. Conversely, everything you stage through the UI can be exported as a manifest and re-applied elsewhere.

Manifests follow the platform-wide [merge-patch write semantics](/reference/#write-semantics): a field absent from the JSON stays unchanged, an explicit `null` clears the stored value, and `[]` clears a list — see [apply: merge-patch](realm-provisioning#apply-merge-vs-prune). The admin modals stage cleared fields as explicit `null`s automatically.

## Moving config between realms and instances

The dev → stage → prod workflow is: configure and test on dev, **Selective export** the app bundle you care about, then on the target open *Configuration Drafts* → **Start a draft from a manifest**, upload the file, review the plan, apply. Because manifests are merge-patches, the partial file only touches what it contains — everything else on the target stays untouched.

Five rules for the transfer:

- **Ids travel with the export**: every exported entity carries its `Id`, and the transferred apps, clients, roles, groups, service accounts etc. land on the target with the *same* ids, so a consuming application that persists them as foreign keys never has to re-link (see [stable ids](realm-provisioning#stable-ids-across-environments)). The id is what identifies an entity: it updates the one it names, revives it if it was deleted, and — for roles, groups, users, service accounts and positions — renames it when the entry's name differs. Ids that can't be honoured (an immutable key like an app slug, or an id belonging to a different kind of entity) surface as an error entry in the plan before you apply.
- **Never apply a partial manifest with prune** — prune deletes everything absent from the manifest.
- **Secrets don't travel**: confidential clients get a freshly generated secret on the target (shown once at apply); provider secrets and user passwords are added to the manifest by hand if needed.
- **User references are per-realm**: group members and position grants name users who usually don't exist on the target — the selective export strips them by default (absent = unchanged over there). One that is carried anyway names its user by id, so on a realm without that id it is skipped and reported, never matched onto whoever happens to share the name.
- **A partial manifest can only ADD to a realm it was not exported from.** Identity is the id ([ADR 0024](/decisions/0024-a-manifest-identifies-by-id-never-by-name)): an entry updates only the entity its `Id` names, so where the target has an entity of the same name under a *different* id, the apply fails on the duplicate rather than overwriting a stranger — and the plan shows that as an error entry before you apply. To update across realms, transfer the ids (which an export does) rather than the names.

Service accounts transfer **with their credentials** (`Credentials` under the account: client id, scopes, apps, token settings) but **never a secret** — a credential the target lacks is issued there with a fresh secret, shown once at apply next to the ordinary client secrets. The account itself is never pruned or staged-deleted (deleting one kills every credential it owns; that stays a deliberate action in the service-account admin). The `Credentials` list is the desired set: a credential missing from a present list is deleted at apply and shows as a red *Clients* entry in the plan; an absent list leaves them alone. See [service accounts and their credentials](realm-provisioning#service-accounts-and-their-credentials).

## Current limits

- **Renaming** works for roles, groups, users, service accounts and positions — the staged entity carries its id, so the apply renames the entity it names. App slugs, client ids, scope/API names and login-provider slugs cannot be renamed at all (other systems address the entity by them); the admin UI keeps those fields read-only on an existing entity.
- **App permission-catalog entries carry their `Id`**, so a staged rename of `invoice:read` to `invoice:view` is a rename — role grants and resource-server subsets follow the id. A hand-written catalog without ids is matched by `resource:action` and reads as unchanged where it is.
- The PageBuilder is not modelled by the manifest and is managed live in its own admin surface. Service accounts export/import with their credentials, positions with their terminal slots (a slot's terminal-managed client is created by the apply, never edited on its own); the account's delete and a slot's revoke stay live. A scheduled job's configuration and the inbox retention policy stage (`Jobs`, `InboxSettings`); a deployment-wide *system* job shown on the control plane is not realm configuration and saves live, and *Run now* is an action.
- **Deleted users don't come back through an import.** Every other entity revives under its pinned id; a user's deletion runs the account lifecycle (recycle bin, grace, purge), so re-importing a binned user's id fails on purpose — restore the user from the bin first, then apply.
