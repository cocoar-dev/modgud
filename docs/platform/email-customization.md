# Customization — Transactional email

Modgud's built-in OTP, magic-link, password-reset, email-verification, change-request and bootstrap messages use one responsive fixed layout. The PageBuilder is not involved.

## Effective branding

Email branding follows the same request context as the login experience:

1. Application context from the host, or the single Application bound to the OAuth client
2. realm branding
3. built-in Modgud defaults

An Application may override the product name, sender display name, sender address, validated reply-to address, subject prefix, hidden preheader and footer text. The sender address resolves App → realm → the deployment's configured sender (`Email:Smtp:FromAddress` / Postmark), so a realm or App can send from its own domain; making that address deliverable (SPF/DKIM/DMARC, or the Postmark sender signature) is the configuring admin's responsibility. Its effective logo and primary colour are reused for the email header and action buttons. Background-capable code can pass an Application or client context explicitly; it does not have to guess from an ambient hostname.

## Languages and message format

Every built-in template has German and English copy. Request language selects the set, with German as the safe fallback. SMTP and the built-in Postmark fallback carry both `text/html` and `text/plain` alternatives. When Postmark template IDs are configured, the corresponding Postmark template owns its HTML/plain-text parts.

## Security

- Model values are HTML-escaped before substitution, including text rendered inside links and tables.
- CR/LF is removed from dynamic subject values to prevent mail-header injection.
- Logo URLs are emitted only for absolute HTTP(S) URLs resolved by Modgud.
- Primary colours accept only a six-digit hex token in email markup; other CSS forms safely use the built-in button colour.
- Unknown placeholders remain visible in development instead of silently disappearing.
- Plain text is generated from the final rendered message and contains the same user-visible information.
- The sender address accepts a bare `local@domain` only; a display-name form (`Name <addr>`) is rejected so nothing can carry extra header material into the envelope. Whether a custom address is *deliverable* is deliberately not checked here: that depends on the admin's mail provider (SPF/DKIM/DMARC, Postmark sender signature) and is theirs to configure.

## Preview

The realm branding page and the Application settings show a live **email preview**: one tab per built-in template (email code, sign-in link, password reset, email verification, admin invite, change-request notifications), with a German/English switch. It is not a mock-up — the backend renders the real template through the same template store and brand layout a real send uses, with the effective branding (realm, or realm + Application override) and the form's *unsaved* values overlaid, so it tracks what you type. The header shows the resolved sender and reply-to as they will appear in the mailbox; the body is rendered in a sandboxed frame with fictional sample data and inert links.

The same endpoint backs it (`POST /api/admin/realm-settings/email-preview`, `realm-settings:read`), which is the seat for editable templates later on. For delivery QA, the development stack exposes Mailpit on port 8025 and SMTP port 1025.
