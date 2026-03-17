# Realm Setup Flow

Every new realm requires an initial setup before it can be used. This page explains what happens and how to troubleshoot.

## The Flow

```
System Admin creates realm
       ↓
Realm has needsSetup: true
       ↓
First visitor to /realms/{slug}/ → redirected to /realms/{slug}/setup
       ↓
Create first admin account
       ↓
Auto-login → realm is ready
       ↓
needsSetup: false (setup endpoint disabled)
```

## Accessing the Setup

Navigate to:
```
http://your-server/realms/{slug}/
```

If the realm needs setup, you'll be automatically redirected to the setup form. If not, you'll see the login page.

## The Setup Form

- **Username** (required)
- **Password** (required) — must meet password policy
- **Email** (optional)
- **First Name / Last Name** (optional)

Click **"Create Admin Account"** to complete setup.

## After Setup

- The `Admin` role is created in the realm
- The admin user is created and assigned the Admin role
- You're signed in immediately
- The setup endpoint now returns 404 (prevents additional accounts via setup)
- Subsequent users must be created by the realm admin via **Administration > Users**

## Troubleshooting

### "needsSetup: false" but no admin exists

This can happen if:
- A previous setup attempt partially succeeded (role was created but user creation failed)
- Database was manually modified

**Fix:** Clear the realm's event store and projection tables, then retry setup.

### Setup returns HTTP 500

Check the backend logs. Common causes:
- Duplicate key constraint on `mt_doc_rolestate` (stale projection data from failed attempt)
- Database connection issues

**Fix:** Truncate the realm's `mt_doc_rolestate`, `mt_doc_userstate`, `mt_events`, and `mt_streams` tables, then retry.
