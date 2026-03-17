# Managing Roles

Roles are managed under **Administration > Roles**.

## Built-in Roles

| Role | Purpose |
|------|---------|
| `Admin` | Full access to admin UI and all admin API endpoints |

Additional roles can be created per realm for custom authorization.

## Creating a Role

1. Click **"New Role"**
2. Fill in:
   - **Name** (required) — e.g., `Editor`, `Viewer`, `BillingAdmin`
   - **Description** (optional)
3. Click **"Create"**

## Editing a Role

1. Click on a role in the list
2. Edit name, description
3. Click **"Save Changes"**

## Deleting a Role

Click **Delete** in the role list. Users who have this role will lose it immediately.

::: warning
Deleting the `Admin` role will lock all admins out of the admin UI for that realm. Be careful!
:::

## How Roles Work with OAuth

When a client requests the `roles` scope, the user's role names are included in the identity token as a `role` claim:

```json
{
  "sub": "user-id",
  "role": ["Admin", "Editor"]
}
```

Resource servers can use these claims for authorization decisions.
