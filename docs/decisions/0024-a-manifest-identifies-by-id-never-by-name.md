# A manifest identifies by id, never by name

**Status:** Accepted · **Decided:** 2026-09-10 · **Relates to:** [ADR 0017](./0017-staged-configuration-draft-mode)

## Context

Applying a manifest asks one question per entity before anything else: is this a new entity, or one that already exists here? The applier answers it by trying the entity's `Id` first and then **falling back to its natural key** — a group by `Name`, a client by `ClientId`, a role by `app/name`, a user by email or username. Ten sections work that way, and references between entities resolve the same way.

The fallback is silent, and that is the problem. Applied to a realm that happens to hold an unrelated entity of the same name, the manifest does not create — it **updates that stranger**. Reference lists are replace-semantics, so a group can inherit another group's members and roles, which is a privilege change nobody asked for and nobody sees. The result is indistinguishable from an intended update: a group left with the wrong members looks exactly like a group that was configured that way.

It does not bite the ordinary path. Ids are pinned at create, so a realm first filled from an export carries that export's ids and matches by id forever after; the stage → prod round trip never reaches the fallback. It bites exactly where two realms grew independently, or where someone created an entity in the target by hand — and those are the cases a *partial* export is for.

No better string fixes this. A name, a key, a slug — every one of them can already stand for something else in the target, because the two realms never shared an identity to begin with. Cross-realm identity is not information a string can carry; it either exists as a stable id or it does not exist at all.

That leaves one honest gap, and it is why the fallback was there: a hand-written manifest that creates several entities which reference **each other** has no ids to point with. Requiring authors to invent ids answers it badly — people copy the example, the example's id ends up in three files, and the collision returns wearing a different hat.

## Decision

**Identity is the id. Everywhere, without exception — for matching an entity and for resolving a reference. No name is ever resolved against the realm.**

The authoring gap is closed with a **document-local handle** rather than an invented id. An `Id` whose value begins with `#` is not an id: it is a name for something *inside this file*. It is never stored. The server assigns a real id at create and resolves every `#` reference in the same run against what the file itself declared.

```json
{
  "Roles":  [ { "Id": "#author", "Name": "Author", "App": "acme" } ],
  "Groups": [ { "Id": "9fA1xR…", "Name": "Editors",
                "Roles": [ "#author", { "Id": "3kQ7pM…", "Key": "acme/Reviewer" } ] } ]
}
```

`#` can never be a ShortGuid, so the two meanings cannot be confused, and no new property is needed. Resolution is per reference, so one file freely mixes entities it creates with entities that already exist:

| Reference form | Resolves against | Not found |
|---|---|---|
| a real id | the realm | **skipped** and reported |
| `#handle` | this manifest | **error** |
| a bare name | nothing | **error** |

The split between *skipped* and *error* is the one this codebase already draws elsewhere: a reference the target does not have is a fact about the target and the apply continues without it; a `#` handle the file never declares is the file contradicting itself, and a bare name is a form that no longer exists. Both are the manifest saying something it cannot mean, and both stop the apply.

Around that:

- **`Id` stays optional.** An entity nothing points at needs no id at all — the server assigns one. A handle is only written when something in the file refers to it.
- **A handle declared twice is an error**; a handle declared and never used is fine, because the typo case surfaces as the dangling reference, with a better message.
- **A `Key` next to a real id becomes a verified hint.** It is never followed. When the id resolves to an entity whose current key differs, that is reported — so a stale name in a file cannot mislead a reader, and cannot quietly become wrong.
- **The apply returns the assigned ids** (`AssignedIds`, handle → id), so a hand-written file can be made idempotent after one run without an export.
- **The plan must match the applier.** It performs the same id-first-then-name matching today; left alone it would promise an update where the apply now creates. It also has to flag a `create` whose natural key is already taken locally, so a collision is seen in review rather than at deploy time.

## Consequences

- **A partial export can no longer *update* entities in a realm that was not filled from the same source — only add to it.** Where names collide the apply fails loudly with `Group.NameTaken`, `App.DuplicateSlug` and their siblings, instead of overwriting a stranger. That is the point of the change, and it is also its sharpest edge: an apply that used to succeed by accident now stops. The plan shows it beforehand.

- **Referencing an existing entity requires knowing its id, so it requires an export.** Writing a manifest entirely from scratch stays possible for *new* entities, which use handles. In practice this makes export → edit → apply the only round trip, which is the documented flow already.

- **Hand-written manifests that reference a newly declared entity by its name break.** They must use a handle. Exports are unaffected: they carry real ids on every entity and every reference.

- **`#` becomes a reserved prefix** for entity ids and reference strings. Slugs are `[a-z0-9-]` and email addresses do not start with `#`, so nothing real collides, but it is now a rule rather than an accident.

- **Measured, not estimated:** removing the fallbacks fails 17 of 94 provisioning tests, every one of them with an `… already exists` message — the intended loud error rather than a product defect. All have the same shape: a second, hand-built manifest without ids that expected an in-place update. Roughly a day including the tests, the TestKit's documented example, and the schema description, where `Id` stops being an optional pinning aid and becomes the identity.

- **Uniqueness of display names is deliberately not decided here.** A group's name is unique realm-wide while a role's is unique per app, and a group reaches a *set* of apps so it cannot be keyed the way a role is. That tension is real and unrelated: it is about what two entities may be called, not about which entity a manifest means. It is analysed in the `modgud` knowledge area under `designs/app-scoped-naming-and-manifest-identity` and waits for its own decision.
