# Password

Manage your sign-in password.

## Change

Profile → Security → **Change password**. Type the current one, then the new one twice. Save.

## Forgot it

On the login page: **Forgot password?** Type your username (or email if you don't remember the username). Modgud emails you a password-reset link valid for 24 hours.

Click the link to open a **Set new password** page. Type your new password there directly — there's no sign-in step and no profile visit involved — then sign in as usual with it.

If the email never arrives:

- Check spam folder
- Verify the address you typed is the one on your account (admin can confirm)
- If the address itself is wrong, only an admin can fix it for you

## Recover when you've lost everything

If you have **no email access** AND **no working 2FA**:

- Contact your admin. They can `Send sign-in link` from the user editor (which goes to whatever email they have on file — same problem if it's wrong) or `Set password` directly (they generate a temporary one for you).
- Worst case (no admin available): someone with server access can use the admin's [Recovery CLI](../operate/recovery-cli) to issue you a one-time sign-in link. It does **not** reset your password for you — once signed in, set a new one yourself from Profile → Security.

## Best practices

::: tip Use a password manager
Modgud enforces a minimum policy: at least 8 characters, at least one digit, at least one uppercase letter. That keeps the worst passwords out — but a manager-generated, unique, long passphrase is the floor for anything you care about.
:::

::: tip Account lockout
After 5 failed sign-in attempts in a row, your account is locked for one minute. Brief — but it means a typo-cluster can briefly lock you out. If you hit it, wait a moment and try again.
:::

::: warning Don't reuse this password elsewhere
A breach of any other service that shared this password becomes a breach of your Modgud account. Unique per service is non-negotiable.
:::

## When passwordless is an option

If you've enrolled at least one **passkey**, you can sign in completely without a password — Touch ID / Windows Hello / YubiKey is enough. The password is then a fallback. See [Passkey](./passkey).
