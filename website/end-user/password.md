# Password

Manage your sign-in password.

## Change

Profile → Security → **Change password**. Type the current one, then the new one twice. Save.

## Forgot it

On the login page: **Forgot password?** Type your username (or email if you don't remember the username). Cocoar.Auth emails you a magic link valid for ~30 minutes.

Click the link → you're signed in (no password needed) → set a new password from your profile.

If the email never arrives:

- Check spam folder
- Verify the address you typed is the one on your account (admin can confirm)
- If the address itself is wrong, only an admin can fix it for you

## Recover when you've lost everything

If you have **no email access** AND **no working 2FA** AND **no recovery codes**:

- Contact your admin. They can `Send sign-in link` from the user editor (which goes to whatever email they have on file — same problem if it's wrong) or `Set password` directly (they generate a temporary one for you).
- Worst case (no admin available): the admin's [Recovery CLI](../admin/recovery-cli) is the last fallback. Someone with container access can reset your password without the UI.

## Best practices

::: tip Use a password manager
Cocoar.Auth doesn't enforce password rules beyond non-empty — that doesn't mean any password is fine. Use a manager to generate a unique, long passphrase per service.
:::

::: warning Don't reuse this password elsewhere
A breach of any other service that shared this password becomes a breach of your Cocoar.Auth account. Unique per service is non-negotiable.
:::

## When passwordless is an option

If you've enrolled at least one **passkey**, you can sign in completely without a password — Touch ID / Windows Hello / YubiKey is enough. The password is then a fallback. See [Passkey](./passkey).
