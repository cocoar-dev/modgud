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

::: tip Too many wrong passwords
Failed attempts are counted per browser. A typo-cluster on your own device is generous (10 in 15 minutes); from a browser that never signed in as you, 5 wrong passwords refuse further attempts for 15 minutes — and you get an e-mail with a sign-in link. Your own devices are never locked out by someone else's attempts.
:::

::: warning Don't reuse this password elsewhere
A breach of any other service that shared this password becomes a breach of your Modgud account. Unique per service is non-negotiable.
:::

## When passwordless is an option

If you've enrolled at least one **passkey**, you can sign in completely without a password — Touch ID / Windows Hello / YubiKey is enough. The password is then a fallback. See [Passkey](./passkey).
