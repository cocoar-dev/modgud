# Customization — Transactional email

Modgud's built-in OTP, magic-link, password-reset, email-verification, change-request and bootstrap messages use one responsive fixed layout. The PageBuilder is not involved.

## Effective branding

Email branding follows the same request context as the login experience:

1. Application context from the host, or the single Application bound to the OAuth client
2. realm branding
3. built-in Modgud defaults

An Application may override the product name, sender display name, validated reply-to address, subject prefix, hidden preheader and footer text. The envelope/from address remains deployment-controlled and verified. Its effective logo and primary colour are reused for the email header and action buttons. Background-capable code can pass an Application or client context explicitly; it does not have to guess from an ambient hostname.

## Languages and message format

Every built-in template has German and English copy. Request language selects the set, with German as the safe fallback. SMTP and the built-in Postmark fallback carry both `text/html` and `text/plain` alternatives. When Postmark template IDs are configured, the corresponding Postmark template owns its HTML/plain-text parts.

## Security

- Model values are HTML-escaped before substitution, including text rendered inside links and tables.
- CR/LF is removed from dynamic subject values to prevent mail-header injection.
- Logo URLs are emitted only for absolute HTTP(S) URLs resolved by Modgud.
- Primary colours accept only a six-digit hex token in email markup; other CSS forms safely use the built-in button colour.
- Unknown placeholders remain visible in development instead of silently disappearing.
- Plain text is generated from the final rendered message and contains the same user-visible information.

Use the Application Settings preview for a quick visual check. For delivery QA, the development stack exposes Mailpit on port 8025 and SMTP port 1025.
