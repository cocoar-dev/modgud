# Scopes & Permissions

Scopes are managed under **Administration > Scopes**.

## Built-in Scopes

These scopes are seeded when a realm is created:

| Scope | Description | Claims Added |
|-------|-------------|-------------|
| `openid` | Required for OIDC | `sub` (user ID) |
| `profile` | User's name | `name`, `given_name`, `family_name` |
| `email` | Email address | `email`, `email_verified` |
| `roles` | Role memberships | `role` (array) |
| `offline_access` | Allows refresh tokens | _(enables refresh token grant)_ |

## Custom Scopes

You can create custom scopes for application-specific permissions:

1. Click **"New Scope"**
2. Fill in:
   - **Name** (required) — e.g., `billing:read`, `reports:write`
   - **Display Name** — shown on consent screen
   - **Description** — explains to users what this permission grants
3. Click **"Create"**

### Example

For a billing API, you might create:
- `billing:read` — "View your invoices and payment history"
- `billing:write` — "Create and modify billing settings"

A client would then request `scope=openid billing:read billing:write`.

## Scopes on the Consent Screen

When a user logs in via OAuth for the first time, they see a consent screen listing the requested scopes. They can review what data the application will access before granting permission.
