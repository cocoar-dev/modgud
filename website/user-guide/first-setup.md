# First-Time Setup

When Cocoar.Auth starts for the first time, no admin account exists. The system guides you through the initial setup.

## Steps

1. **Navigate to the application** — Open `http://localhost:4200/` (or your deployment URL)
2. **You'll see the Setup page** — "Initial Setup: Create the first administrator account"
3. **Fill in the form**:
   - **Username** (required) — e.g., `admin`
   - **Password** (required) — must meet password policy (uppercase, lowercase, digit, special char, min 8 chars)
   - **Email** (optional) — for password recovery
   - **First Name / Last Name** (optional)
4. **Click "Create Admin Account"**
5. **You're automatically logged in** as the system admin

## What Happens Behind the Scenes

- The `Admin` role is created in the system realm
- Your user account is created with the `Admin` role
- You're signed in with a session cookie
- The setup endpoint becomes unavailable (returns 404)

## Next Steps

After initial setup, you can:
- [Create additional realms](/user-guide/realms) for multi-realm isolation
- [Add more users](/user-guide/users) to the system realm
- [Register OAuth clients](/user-guide/clients) for external applications
- Set up [two-factor authentication](/user-guide/two-factor) for your account
