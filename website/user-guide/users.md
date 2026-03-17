# Managing Users

User management is available to realm admins under **Administration > Users**.

## Listing Users

The user list shows all users in the current realm with:
- Username, First Name, Last Name, Email
- Status (Active / Inactive)
- Creation date

Use the search bar to filter by username, email, or name.

## Creating a User

1. Click **"New User"**
2. Fill in:
   - **Username** (required)
   - **Password** (required)
   - **Email** (optional)
   - **First Name / Last Name** (optional)
   - **Roles** — assign roles at creation time
   - **Active** — enable/disable the account
3. Click **"Create"**

## Editing a User

1. Click on a user in the list
2. Edit any field (username, email, profile, roles, active status)
3. Click **"Save Changes"**

### Admin Actions (available in the list grid)

- **Reset Password** — set a new password for the user
- **Unlock** — unlock a user locked out due to failed login attempts
- **Force Logout** — revoke all of the user's active sessions
- **Soft Delete** — anonymize user (GDPR soft delete)
- **Restore** — reverse a soft delete
- **Permanent Erasure** — irreversible GDPR data removal

::: warning
Delete and admin actions are only available in the grid, not in the detail form. The detail form footer only has Back and Save.
:::

## Roles

Users can be assigned one or more roles. The `Admin` role is special — it grants access to the admin UI and all admin API endpoints within the realm.
