# Groups

Groups are the **organisational layer** in Cocoar.Auth. They serve two distinct purposes — and you decide per-group which one applies via the **Bound to apps** field.

1. **Authorisation grouping.** Members of the group inherit the group's roles in the apps the group is bound to.
2. **Mailing-list / distribution semantics.** Even a group with no roles and no app binding can carry an email address, expand to its members, and be addressed by notification flows.

A user can be a member of any number of groups; a group can be a member of another group (transitive resolution).

![Groups list](/screenshots/admin-gruppen-liste.png)

## Why groups?

Roles answer "what may you do"; groups answer "who is this user, organisationally". Splitting the two means you can change a person's department without touching their permissions, or change a permission set without re-onboarding everyone.

The strict path from user to permission is:

```
User → Group(s) → Role(s) → Permission(s)
```

Direct user-to-role or user-to-permission assignments don't exist. Membership-via-group is the sole route.

## Creating a group

Administration → **Groups** → **Create**.

Tabs in the detail dialog:

| Tab | Content |
| --- | --- |
| **General** | Name, description, **Bound to apps**, membership mode |
| **Members** | Manual user / sub-group assignment (when membership is Static) |
| **Script** | JsEval membership script (when membership is Auto) |
| **Roles** | Which roles does the group carry? |
| **Access** | Per-resource access scripts (ABAC row filters) |
| **Effective Members** | The fully expanded member list |

### General

- **Name** (unique)
- **Description** (optional)
- **Membership mode**:
  - **Static** — you maintain members manually on the Members tab
  - **Auto (script)** — membership is computed by a script
  - **Hybrid** — static members plus auto-computed additions
- **Bound to apps** — *(MultiSelect)* which apps does this group take effect in? See below.

#### Bound to apps — the activation switch

A group can have members and roles without taking effect for permissions. The decision is in **Bound to apps**:

| Selection | Effect |
| --- | --- |
| **★ All apps (\*)** | Wildcard — the group is active in **every** app. Typical for the realm-admin group. |
| One or more concrete apps | The group only contributes when the requesting app is in this list. |
| **empty** | Group is *dormant* for permission purposes — it counts nowhere. Useful for purely organisational groups like mailing lists ("HR team", "Vienna office"). |

**Practical behaviour:** you can temporarily remove an app from the list (e.g. during maintenance) without losing role assignments. Re-adding the app reactivates the group immediately. BoundTo changes never cascade-delete the group's roles.

**Default for new groups:** `[cocoar-auth]`. When creating a group for another app, remember to update the selection.

## Static membership

Tab **Members** shows two listboxes (drag-and-drop): all principals (users + sub-groups) on the left, current members on the right.

Sub-group memberships **are transitive**. If `Vienna Office` contains `Sales-Vienna` which contains user `Max`, Max effectively belongs to all three.

## Auto membership (membership scripts)

Switch the mode to **Auto** to enable the **Script** tab. There you write a JsEval expression that returns `true`/`false` per principal:

```javascript
// Example: "all users with @cocoar.io email"
return p.Type === "person" && p.Email && p.Email.endsWith("@cocoar.io");
```

The script is recompiled and re-evaluated whenever a principal is created or changed. For the full reference see [Access Scripts (ABAC)](../authorization-slice/access-scripts).

## Assigning roles

Tab **Roles**: pick the roles the group should carry. A group can hold roles from **multiple apps** simultaneously — but they only contribute in apps where the group's BoundTo matches the role's AppSlug.

> Example: a group `DevOps Team` with `BoundTo: ["timetodo", "knowledge"]` and roles `[timetodo-admin, knowledge-author]`. When a `timetodo` permission lookup runs, only the `timetodo-admin` role contributes. When a `knowledge` permission lookup runs, only `knowledge-author` does.

## Effective members

Tab **Effective Members** shows the fully expanded list — direct members plus everyone reached through nested groups, with a "via" hint pointing at the first nested-group hop. Useful for sanity checks before granting a powerful role.

## Deleting a group

List → right-click → **Delete**.

::: warning Soft delete
Groups are soft-deleted. Users who were members keep the membership entry technically — but the group contributes nothing any more. To clean up properly, also remove the group from any parent groups first.
:::

## Email & notifications

Groups can carry an email address (Tab General, optional). Notification flows can address `<group-name>@…` and Cocoar.Auth resolves the recipient list:

- **Shared** mode — mail goes to the group's own address (a shared mailbox, distribution list)
- **Expand to members** mode — mail goes to each member's individual email, recursively across nested groups

Cycle-safe: a group `A` containing `B` containing `A` is detected; expansion stops at the first revisit.

## What happens when a user is in multiple groups?

All rights are **unioned**. If you're in two groups with different roles, you hold the combined permissions. There's no priority between groups.

If two groups bring different `BoundTo` lists, both are evaluated independently — the user is "active" in any app that any of their groups covers.
