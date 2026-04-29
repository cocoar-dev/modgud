# Auth-Endpoints

Endpoints unter `/api/account/...`. Die aktuelle Realm wird über das
**Host-Header** aufgelöst — keine Realm-Pfad-Prefixes.

Vollständige Endpoint-Liste in
`src/dotnet/Cocoar.Auth.Authentication/Api/Account/`.

## Public Authentication

| Method | Path | Beschreibung |
|---|---|---|
| `POST` | `/api/account/login` | Login mit Username + Passwort |
| `POST` | `/api/account/logout` | Logout (cookie weg, Session invalidieren) |
| `POST` | `/api/account/register` | Selbst-Registrierung |
| `POST` | `/api/account/forgot-password` | Password-Reset-Link anfordern |
| `POST` | `/api/account/reset-password` | Password mit Token zurücksetzen |
| `GET` | `/api/account/confirm-email` | E-Mail bestätigen via Token-Link |
| `POST` | `/api/account/resend-confirmation` | Bestätigungs-Mail erneut senden |

## Current User & Profile

| Method | Path | Beschreibung |
|---|---|---|
| `GET` | `/api/account/me` | Aktuelle User-Info (inkl. effective Permissions, Realm-Slug) |
| `GET` | `/api/account/profile` | Detail-Profil |
| `PUT` | `/api/account/profile` | Profil ändern (legt UserChangeRequest an) |
| `POST` | `/api/account/change-password` | Passwort ändern |
| `GET` | `/api/account/profile/links` | Verknüpfte OIDC-Identitäten |
| `POST` | `/api/account/external-link/{idpConfigId}/start` | Account-Linking initiieren |
| `DELETE` | `/api/account/external-link/{linkId}` | Verknüpfung aufheben |

## Two-Factor Authentication

### Status & TOTP

| Method | Path | Beschreibung |
|---|---|---|
| `GET` | `/api/account/mfa/status` | 2FA-Status (enabled, methods, recoveryCodesRemaining) |
| `POST` | `/api/account/mfa/setup` | TOTP-Authenticator-Key + QR-URI generieren |
| `POST` | `/api/account/mfa/enable` | 2FA mit TOTP-Code aktivieren |
| `POST` | `/api/account/mfa/disable` | 2FA deaktivieren |
| `POST` | `/api/account/mfa/recovery-codes` | Recovery-Codes regenerieren |
| `POST` | `/api/account/mfa/login` | Login Schritt 2 mit TOTP-Code |
| `POST` | `/api/account/mfa/recovery-login` | Login mit Recovery-Code |

### Email OTP

| Method | Path | Beschreibung |
|---|---|---|
| `GET` | `/api/account/email-otp/status` | Email-OTP-Status |
| `POST` | `/api/account/email-otp/login/request` | Email-OTP für Login anfordern |
| `POST` | `/api/account/email-otp/login` | Login mit Email-OTP |

### Passkey / FIDO2 / WebAuthn

| Method | Path | Beschreibung |
|---|---|---|
| `POST` | `/api/account/passkey/register/options` | Registration-Options |
| `POST` | `/api/account/passkey/register/complete` | Registration abschließen |
| `POST` | `/api/account/passkey/login/options` | Login-Options (mit oder ohne userName für passwordless) |
| `POST` | `/api/account/passkey/login/complete` | Login abschließen |
| `GET` | `/api/account/passkey/credentials` | Eigene Passkeys auflisten |
| `DELETE` | `/api/account/passkey/credentials/{id}` | Passkey löschen |
| `PATCH` | `/api/account/passkey/credentials/{id}` | Passkey-Label ändern |

### Magic Link

| Method | Path | Beschreibung |
|---|---|---|
| `POST` | `/api/account/magic-link/request` | Magic-Link Self-Service anfordern (nur wenn enabled) |
| `GET` | `/api/account/magic-link/login?token=...&user=...` | Magic-Link-Login |

## External Login (OIDC)

| Method | Path | Beschreibung |
|---|---|---|
| `GET` | `/api/account/external-login/providers` | Liste aktiver IdpConfigs (kein Secret) |
| `GET` | `/api/account/external-login/{idpConfigId}/start?returnUrl=/` | OIDC-Flow starten |
| `GET` | `/api/account/external-login/callback` | OIDC-Callback (vom externen IdP) |

### Login-Flow

```
1. Frontend: GET /api/account/external-login/providers → zeigt Provider-Buttons
2. User klickt "Login with Acme SSO" (= IdpConfig "acme-sso")
3. Browser: GET /api/account/external-login/{id}/start?returnUrl=/
4. Backend: ASP.NET Challenge mit dynamisch registriertem OIDC-Scheme
5. Browser: 302 → externer IdP
6. User authentifiziert sich beim IdP
7. IdP: 302 → /api/account/external-login/callback
8. Backend: ExternalLoginProcessor läuft (User suchen oder JIT-Create,
   UserUpdateScript ausführen, Login-Cookie setzen)
9. Backend: 302 → returnUrl
```

## Sessions

| Method | Path | Beschreibung |
|---|---|---|
| `GET` | `/api/account/sessions` | Aktive Sessions |
| `DELETE` | `/api/account/sessions/{id}` | Session revoken |
| `DELETE` | `/api/account/sessions` | Alle Sessions revoken außer current ("logout everywhere") |

## GDPR / Privacy

| Method | Path | Beschreibung |
|---|---|---|
| `GET` | `/api/account/gdpr/export` | Daten-Export (Article 20) — ZIP |
| `GET` | `/api/account/gdpr/delete-status` | Status des Delete-Workflows |
| `POST` | `/api/account/gdpr/delete-request` | Account-Löschung beantragen (Mail mit Token geht raus) |
| `POST` | `/api/account/gdpr/delete-confirm` | Mit Token bestätigen → Stream archivieren + PII masken |
| `POST` | `/api/account/gdpr/delete-cancel` | Pending Delete-Request canceln |

## Setup (First-Time)

| Method | Path | Beschreibung |
|---|---|---|
| `GET` | `/api/setup/status` | `{ needsSetup: bool }` — true wenn noch kein Admin existiert |
| `POST` | `/api/setup/create-admin` | First-Time-Admin anlegen, auto-login |

Beim ersten Realm-Aufruf zeigt das Frontend `/setup`. Nach dem ersten
Admin sind `/api/setup/*` 404.

## Response-Format-Konventionen

- Alle Responses verwenden **PascalCase** JSON
  (`PropertyNamingPolicy = null`)
- `null`-Felder werden weggelassen (`JsonIgnoreCondition.WhenWritingNull`)
- Enums werden als String serialisiert
- Errors als `ProblemDetails` (`application/problem+json`)

## Auth-Status-Codes

| Status | Bedeutung |
|---|---|
| `200 { authenticated: true, ... }` | Erfolgreich (Cookie gesetzt) |
| `200 { requiresTwoFactor: true, mfaMethods: [...] }` | Schritt-2-MFA nötig |
| `200 { requiresSecureSetup: true, gracePeriod: true, secureSetupDueAt }` | User muss noch 2FA einrichten, hat Zeit |
| `200 { requiresSecureSetup: true, gracePeriod: false }` | Grace-Period vorbei, blocking |
| `401` | Nicht authentifiziert oder Credentials falsch |
| `403` | Authentifiziert aber keine Permission, oder Passwordless-only-Realm |
| `429` | Rate-Limit (Email-OTP, Magic-Link) |
