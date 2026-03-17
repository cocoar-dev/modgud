# Managing Realms

Realm management is only available to **system realm admins**. The "Realms" menu appears under the **System** section in the sidebar.

## Creating a Realm

1. Navigate to **System > Realms** in the sidebar
2. Click **"New Realm"**
3. Fill in:
   - **Slug** (required) — lowercase, letters/numbers/hyphens, e.g. `acme`. This cannot be changed later. It becomes part of the realm's URL: `/realms/acme/`
   - **Display Name** (required) — human-readable name, e.g. "Acme Corporation"
   - **Description** (optional)
4. Click **"Create"**

### What Happens

- A dedicated PostgreSQL database is created (`cocoar_auth_acme`)
- Database schema is applied (all Marten tables, indexes, functions)
- Default OAuth scopes are seeded (openid, email, profile, roles, offline_access)
- Default login providers are configured
- The realm appears in the list with **"Needs Setup"** status

## Setting Up a New Realm

After creating a realm, it needs its first admin:

1. Open `/realms/{slug}/` in a **new browser tab** (e.g., `http://localhost:4200/realms/acme/`)
2. You'll be redirected to the **Initial Setup** page
3. Create the realm's first admin account (same form as system setup)
4. You're auto-logged-in as the realm admin

::: tip Independent Admin Accounts
The realm admin is completely independent from the system admin. They have their own username, password, and credentials. A person could use the same username in different realms — they are separate accounts.
:::

## Editing a Realm

1. Click on a realm in the list (or click Edit)
2. You can change:
   - **Display Name**
   - **Description**
3. Click **"Save Changes"**

::: warning
The **slug** cannot be changed after creation (it's part of URLs, database names, and cookie paths).
:::

## Deactivating a Realm

Deactivating a realm makes it inaccessible — all API requests return 404. The data is preserved.

Edit the realm and set `isActive` to false via the API:
```
PATCH /api/admin/realms/{slug}
{ "isActive": false }
```

::: danger
The system realm cannot be deactivated.
:::

## Deleting a Realm

Click the **Delete** button in the realm list (not available for the system realm).

::: warning Current Limitation
Realm deletion currently removes the realm metadata but does not drop the realm's database. Full cleanup (including database deletion) requires Wolverine daemon coordination, which is planned for a future release.
:::

## Realm Indicator

The sidebar always shows which realm you're currently operating in:

- **System realm**: Shows `REALM system` with the full admin menu including "Realms"
- **Other realms**: Shows `REALM acme` (or whatever the slug is) without the "Realms" menu
