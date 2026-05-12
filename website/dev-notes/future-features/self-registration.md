# Self-Registration — Handoff

> **Status:** Backend-MVP implementiert 2026-05-12. Frontend offen.
> **Trigger:** User-Anfrage „Selbst-Registration, pro Tenant konfigurierbar". Voll-MVP-Scope (Phase 1) durchgegangen + Captcha als Pflicht-Tier gewählt.
> **Diese Note ist ein Handoff für die nächste Session.** Sie wiederholt die Designentscheidungen, listet was im Code steht, und beschreibt was Phase 3 (Frontend) braucht.

## Designkonsens (aus der Session)

### Was Self-Registration in unserem Modell ist

Ein anonymer Endpoint per Realm: User füllt Form mit Username + Email + Password, optional ToS-Häkchen, optional Captcha. Backend legt User-Record mit `EmailConfirmed=false` an, schickt Verification-Mail. Click auf Magic-Link → `EmailConfirmed=true` + optional Admin-Approval-Gate + Auto-Attach zu Default-Groups (Role-Membership cascadet über Groups).

Pro Realm konfigurierbar, **opt-in** (Default `Enabled=false` → keine `/register`-Route nutzbar).

### Pro-Realm-Settings (alle auf `Realm.SelfRegistration`)

| Feld | Default | Beschreibung |
|---|---|---|
| `Enabled` | `false` | Master-Toggle |
| `RequireEmailVerification` | `true` | User kann erst nach Magic-Link-Click loggen. `false` = auto-confirm bei Anlage (für trusted-intern). |
| `AllowedEmailDomains: string[]` | `null` | Whitelist; `null`/empty = alle |
| `RequireAdminApproval` | `false` | Auch nach Email-Confirm bleibt `IsActive=false`, Admin muss erst freischalten |
| `DefaultGroupIds: string[]` | `null` | Auto-Attach bei Aktivierung; leer = keine Group (kein Role-Cascade) |
| `TermsOfServiceUrl` | `null` | Wenn gesetzt: Form zeigt Pflicht-Checkbox |
| `PrivacyPolicyUrl` | `null` | Footer-Link |
| `CaptchaEnabled` | `false` | **Eigener Toggle**, getrennt vom Master. Lässt intern-Deployments ohne Cloudflare-Egress laufen. |
| `CaptchaSiteKey` | `null` | Per-Realm-Override; null + `CaptchaEnabled=true` → Fallback Cocoar-default |
| `EncryptedCaptchaSecret: byte[]?` | `null` | DataProtection-encrypted per-realm Secret; null + `CaptchaEnabled=true` → Fallback Cocoar-default |

### Username-Strategie

**Form fragt Username UND Email** (user-Auswahl b). Username-Collision wird explizit gemeldet — Username ist public-shape (anders als Email, das anti-enumerated ist).

### Captcha — Cloudflare Turnstile

- Provider-only-Turnstile (keine Abstraktion).
- **Cocoar-default-Pattern**: System-weite Keys in `TurnstileSettings` (Cocoar.Configuration env-vars `Turnstile__SiteKey` / `Turnstile__SecretKey`). Per-Realm-Overrides via `Realm.SelfRegistration.CaptchaSiteKey` + `EncryptedCaptchaSecret`. Resolution-Order:
  1. Per-Realm-Keys wenn gesetzt
  2. Sonst Cocoar-default aus `TurnstileSettings`
  3. Sonst (mit `CaptchaEnabled=true`) → Verifier rejected → registration fails. Logs WARN-Level damit der Admin's findet.
- **Air-gapped-OK**: wenn ein Realm `CaptchaEnabled=false` setzt, ruft Cocoar.Auth nie `challenges.cloudflare.com` an. Honeypot + Email-Rate-Limit als Defense-in-Depth.

### Anti-Enumeration

- Existierende Email → trotzdem `200 OK` mit „Falls die Registrierung gültig ist, kommt eine E-Mail" + KEIN Mail-Send. Bestandskunden werden nicht „you already have account" gespoiled.
- Existierender Username → gleiches generisches `200 OK`. (Frontend macht aber pre-submit-Validation via Live-Check; siehe Phase 3.)
- Captcha-Failure / Honeypot-Trigger / Rate-Limit / Domain-Whitelist-Fail → alle generischer `200 OK`.

### Email-Verification-Flow

- Token-Format: 32 random bytes → Base64Url-encoded (43 chars). Stored als SHA-256-Hex. Plaintext nur im Magic-Link.
- TTL: 24h (`PendingSelfRegistration.DefaultExpirationHours`).
- URL-Shape: `{publicUrl}/verify-email?token=<plaintext>` — die Frontend-Route `/verify-email` macht den POST gegen `/api/account/register/verify-email`.
- Template reuse: `EmailTemplate.EmailVerification` (existiert schon, generisch genug — Variablen `AppName` / `DisplayName` / `ActionUrl` / `ExpirationHours`).

## Was im Backend-Code steht

### Domain (Cocoar.Auth.Domain)

- `Realms/Realm.cs` — neues Feld `SelfRegistrationSettings? SelfRegistration { get; set; }`
- `Realms/SelfRegistrationSettings.cs` — neuer Record (POCO, kein Aggregate, Marten-JSONB-storage)

### Application (Cocoar.Auth.Application)

- `DTOs/Realms/RealmDtos.cs` — `SelfRegistrationDto` (read), `UpdateSelfRegistrationDto` (PATCH, three-state-secret-idiom)
- `DTOs/SelfRegistration/SelfRegistrationDtos.cs` — `SelfRegistrationInfoDto`, `RegisterDto`, `RegisterResponseDto`

### Authentication (Cocoar.Auth.Authentication) — neue Slice `SelfRegistration/`

- `Captcha/CaptchaSecretStore.cs` — DataProtection-Wrapper mit Purpose `Cocoar.Auth.SelfRegistration.CaptchaSecret.v1`
- `Captcha/ITurnstileSecretResolver.cs` + `TurnstileSecretResolver.cs` — Fallback-Chain (per-realm → system-default delegate → null)
- `Captcha/TurnstileVerifier.cs` — HTTP-Client wrapper. Retoure: `Skipped` / `Verified` / `Failed`. Tritt Cloudflare-Endpoint `challenges.cloudflare.com/turnstile/v0/siteverify` an.
- `Domain/PendingSelfRegistration.cs` — Marten-Doc, tenant-scoped, hält Token-Hash + UserId + Default-Group-Snapshot
- `RegistrationRateLimiter.cs` — in-memory `ConcurrentDictionary<email, attempts>` sliding-window (1/min + 3/h)
- `SelfRegistrationService.cs` — Orchestrator. Zwei Methoden: `RegisterAsync` + `VerifyEmailAsync`. Alle Side-Effects (Identity-Create, Token-Issue, Email-Send) im Service, Endpoint ist dünn.
- `Api/Account/RegisterEndpoints.cs` — Drei Routen alle anonymous:
  - `GET /api/account/self-registration-info`
  - `POST /api/account/register`
  - `POST /api/account/register/verify-email`

### Infrastructure (Cocoar.Auth.Infrastructure)

- `Realms/RealmProvisioningService.cs` — `UpdateRealmAsync` kriegt jetzt `Func<string, byte[]>? captchaSecretEncryptor` Parameter. Static `ApplySelfRegistrationPatch`-Helper macht das field-wise-merge inkl. three-state-secret-behavior.

### Api (Cocoar.Auth.Api)

- `TurnstileSettings.cs` — POCO für die Cocoar-default keys
- `Features/Admin/RealmsEndpoints.cs` — `MapToDto` + `MapSelfRegistrationToDto`. PATCH-Endpoint injectet `CaptchaSecretStore` und ruft Provisioning mit dem `Encrypt`-delegate.
- `Program.cs`:
  - `TurnstileSettings`-Binding (Cocoar.Configuration, env-var `Turnstile__SiteKey/SecretKey`)
  - DI: `CaptchaSecretStore`, `TurnstileVerifier`, `ITurnstileSecretResolver` (mit system-default Delegates aus `TurnstileSettings`), `RegistrationRateLimiter` (singleton), `ISelfRegistrationService` (scoped)
  - `MapRegisterEndpoints("api")` nach `MapPasswordResetEndpoints` eingehängt

## Was Phase 3 (Frontend) braucht

### Routen + Views

- **`/register`** — public Route, `RegisterView.vue`:
  - onMount: `GET /api/account/self-registration-info`
  - Wenn `Enabled=false`: redirect `/login`
  - Form: Username, Email, Password, optional Firstname/Lastname, ToS-Checkbox wenn `TermsOfServiceUrl` da, Turnstile-Widget wenn `CaptchaSiteKey` da, **Honeypot** (hidden field, name doesn't matter — auf der DTO ist `Honeypot: string?`)
  - Submit: `POST /api/account/register` mit dem Form-Body
  - Success: zeige `RegisterResponseDto.Message` als CoarNote, bleib auf Seite oder go to /login
- **`/verify-email`** — public Route, `VerifyEmailView.vue`:
  - Liest `?token=...` aus der URL
  - `POST /api/account/register/verify-email` mit `{ token }`
  - Success-Response = `VerifyEmailResponseDto { UserName, Email, RequiresAdminApproval }`
  - Wenn `RequiresAdminApproval=true`: zeige „Account verifiziert, wartet auf Admin-Freischaltung"
  - Sonst: „Account verifiziert" + Auto-Redirect oder Link `/login`
  - Error-Responses: alle als CoarNote-Error sichtbar machen (real errors, kein Anti-Enum hier)

### Login → Register-Link

- `LoginView.vue` lädt zum Mount `/api/account/self-registration-info`. Wenn `Enabled=true`: zeige unter dem Login-Form einen Link „Noch kein Konto? Registrieren →" der zu `/register` führt.

### Admin: RealmDetails Self-Registration-Section

- `RealmDetails.vue` (`views/admin/realms/`) hat heute keine Tabs — einfach eine neue Section ans Ende anhängen, vor `IsControlPlane`-Section.
- Felder (Pinia bindings via die existierende `realm.store` + erweiterte `RealmDto` model):
  - `Enabled` — Checkbox
  - `RequireEmailVerification` — Checkbox (default true)
  - `RequireAdminApproval` — Checkbox
  - `AllowedEmailDomains` — `<EditableStringList>` (existing component)
  - `DefaultGroupIds` — Picker (Multi-Select aus `useGroupStore`-Inhalt)
  - `TermsOfServiceUrl`, `PrivacyPolicyUrl` — CoarTextInput
  - `CaptchaEnabled` — Checkbox
  - `CaptchaSiteKey` — CoarTextInput (nur sichtbar wenn `CaptchaEnabled`)
  - `CaptchaSecret` — Write-Only-Pattern: zeige `••••• (gesetzt)` wenn `CaptchaSecretSet=true`, sonst „(noch nicht gesetzt)". Button „Ersetzen" zeigt ein Input. Input mit empty Submit = clear (fall back to default). Send als String, Backend kümmert sich um Encryption.
- Save-Path: PATCH `/api/admin/realms/{slug}` mit `{ SelfRegistration: { ... } }`. UpdateSelfRegistrationDto-Felder sind alle nullable → omit fields the user hasn't touched.

### Turnstile-Widget-Integration

```html
<script src="https://challenges.cloudflare.com/turnstile/v0/api.js" async defer></script>
<div class="cf-turnstile" data-sitekey="{{siteKey}}" data-callback="onCaptchaSuccess"></div>
```

- Script-Tag dynamisch eingehängt wenn `CaptchaSiteKey` non-null
- Vue-wrapper: hör auf `data-callback` via `(window as any).onCaptchaSuccess = (token) => form.captchaToken = token`
- Bei Reset (z.B. Form-Submit-Error): `(window as any).turnstile.reset()`
- Alternative: gibt fertige Vue-Wrapper auf npm (`vue-turnstile`), aber das ist eine weitere Dependency

### Store / Model-Erweiterungen

- `models/realm.ts` — `RealmDto.SelfRegistration: SelfRegistrationDto`
- `stores/realm.store.ts` — bereits `update(slug, dto)` da; nur den DTO-Shape erweitern
- Neue `stores/selfRegistration.store.ts` (optional) oder direkt http-Calls in `RegisterView`/`VerifyEmailView`

## Known limitations / TODOs

- **Kein Admin-Approve-UI**: `RequireAdminApproval=true` macht User `IsActive=false`. Aktuell kein dedizierter Approve-Endpoint — Admin muss über die existierende User-Edit-UI `IsActive=true` setzen. Eigener „Pending Approvals"-Filter im User-Grid wäre ein netter Add-On.
- **Keine PATCH-Validation für captcha-enabled-ohne-keys**: bewusst weggelassen für MVP. Im Worst-Case enabled der Admin Captcha ohne Keys → Verifier rejected jeden Register → User merkt's beim Testen, Admin findet's im WARN-Log. Nice-to-have ist Validation am PATCH-Endpoint die das vorbeugend ablehnt.
- **In-memory Rate-Limiter**: resetet bei App-Restart. Multi-Instance-Setups würden das umgehen können. Wenn relevant: Redis-backed-impl hinter derselben Schnittstelle.
- **Email-Template**: reused `EmailTemplate.EmailVerification` (war ursprünglich für E-Mail-Adress-Change-Verifikation gedacht, Wording ist „Sie haben angefragt, diese E-Mail für Ihr {{AppName}}-Konto zu hinterlegen"). Funktioniert auch für Register-Verifikation, aber ein dedizierter `EmailTemplate.SelfRegistrationVerify` mit Welcome-Wording wäre netter.
- **Anti-Email-Enumeration mit Username**: heute meldet das Backend Username-Collision via dem gleichen generischen 200-OK. Frontend sollte aber pre-submit-Live-Check anbieten („dieser Benutzername ist bereits vergeben") für UX. Endpoint dafür existiert noch nicht — neuer anon-`GET /api/account/check-username/{name}` müsste her (rate-limited, anti-flood).
- **Email-Verification-View muss noch entscheiden**: nach erfolgreichem Verify → auto-sign-in (kommt mit Identity-Cookie zurück) oder return user to /login? MVP-Empfehlung: einfach zur /login-Seite mit pre-filled username.

## Wichtige Wire-Contracts für Frontend-Implementation

### `GET /api/account/self-registration-info`

Public, no auth. Returns `SelfRegistrationInfoDto`:

```typescript
{
  Enabled: boolean
  RequireEmailVerification: boolean
  RequireAdminApproval: boolean
  AllowedEmailDomains: string[] | null
  TermsOfServiceUrl: string | null
  PrivacyPolicyUrl: string | null
  CaptchaSiteKey: string | null   // null = no captcha to mount
}
```

Wenn `Enabled=false`: alle Felder default-ish (siehe `SelfRegistrationInfoDto` C#-default values). SPA-Logik: `if (!info.Enabled) router.replace('/login')`.

### `POST /api/account/register`

Public, no auth. Body `RegisterDto`:

```typescript
{
  UserName: string
  Email: string
  Password: string
  Firstname?: string | null
  Lastname?: string | null
  AcceptedTerms: boolean
  CaptchaToken?: string | null   // omit when no captcha required
  Honeypot?: string | null       // hidden form field — leave empty in legit submits
}
```

Response immer 200 mit `RegisterResponseDto`:

```typescript
{ Message: string }
```

Anti-Enumeration: gleiche Antwort egal ob success oder rejected. UI zeigt einfach die Message als Erfolgs-Note + leitet zu /login oder /check-your-mail-Seite.

### `POST /api/account/register/verify-email`

Public, no auth. Body:

```typescript
{ Token: string }
```

Response 200 mit `VerifyEmailResponseDto`:

```typescript
{
  UserName: string
  Email: string
  RequiresAdminApproval: boolean
}
```

Oder 4xx/5xx mit Standard-Error-Body (ErrorOr-shape: `{ Code, Description, Type }`):
- `SelfRegistration.TokenRequired` / `TokenUnknown` / `TokenUsed` / `TokenExpired`

### PATCH `/api/admin/realms/{slug}` (admin-side)

Body `UpdateRealmDto` — neues Feld `SelfRegistration: UpdateSelfRegistrationDto?`:

```typescript
{
  // ... existing realm fields
  SelfRegistration?: {
    Enabled?: boolean
    RequireEmailVerification?: boolean
    AllowedEmailDomains?: string[]
    RequireAdminApproval?: boolean
    DefaultGroupIds?: string[]
    TermsOfServiceUrl?: string
    PrivacyPolicyUrl?: string
    CaptchaEnabled?: boolean
    CaptchaSiteKey?: string
    CaptchaSecret?: string   // null = no change; "" = clear; "xxx" = replace
  }
}
```

Response = existing `RealmDto` (jetzt mit `SelfRegistration: SelfRegistrationDto` Feld).

## Wie man's lokal testet (ohne Frontend)

```bash
# 1. Self-reg auf einem Realm aktivieren (curl, als CP-Admin authentifiziert)
curl -X PATCH https://auth.cocoar.dev/api/admin/realms/system \
  -H "Content-Type: application/json" \
  --cookie cocoar.session=<your-session-cookie> \
  -d '{
    "SelfRegistration": {
      "Enabled": true,
      "RequireEmailVerification": true,
      "RequireAdminApproval": false,
      "CaptchaEnabled": false
    }
  }'

# 2. Info-Endpoint checken
curl https://auth.cocoar.dev/api/account/self-registration-info

# 3. Register-Endpoint feuern
curl -X POST https://auth.cocoar.dev/api/account/register \
  -H "Content-Type: application/json" \
  -d '{
    "UserName": "smoketest",
    "Email": "smoketest@example.com",
    "Password": "TestPass1234!",
    "AcceptedTerms": false
  }'

# 4. In Dev: Email landet in InMemoryEmailService. Link aus den Logs lesen,
#    oder den Token direkt aus der Marten-DB ziehen:
docker exec cocoar-postgres psql -U postgres -d cocoar_auth_system \
  -c "SELECT data->>'TokenHash' FROM mt_doc_pendingselfregistration LIMIT 1;"
#    Plaintext-Token gibt's natürlich nicht aus dem Hash — nur via Email/Logs.

# 5. Verify-Endpoint mit dem Plaintext-Token
curl -X POST https://auth.cocoar.dev/api/account/register/verify-email \
  -H "Content-Type: application/json" \
  -d '{ "Token": "<plaintext>" }'
```

## Commits in dieser Session

- `97661ef` — `feat(self-reg): per-realm self-registration settings (foundation)` — Realm-Domain-Field + DTOs + Admin-PATCH-Wiring
- (next) — Phase 2 backend service + endpoints + DI

## Wenn Frontend gestartet wird

1. Erst diese Note durchlesen.
2. **Stack-Reminder:** Vue 3, Pinia, Tailwind, `@cocoar/vue-ui` 1.18.0, `@cocoar/vue-data-grid` 1.18.0, useHttpClient für Endpoint-Calls, `<EditableStringList>` für AllowedEmailDomains.
3. Existing Patterns folgen:
   - `LoginView.vue` zeigt wie ein anon-form heute aussieht
   - `MagicLoginView.vue` zeigt wie ein verify-token-from-url-Flow heute aussieht
   - `RealmDetails.vue` ist das bestehende Single-Form-Admin-Modal — Self-Reg-Section dort als neue Section anhängen
4. Turnstile-Widget via `<script>`-Tag laden, mit Callback in `window.onCaptchaSuccess`.
5. Realm-DTO-Model erweitern.
6. **Was NICHT vergessen:** der Honeypot-Field. Hidden Field mit autocomplete=off, name irgendwas-Plausibles wie `website` oder `phone-number-confirm`. Niemals als für Menschen sichtbar rendern.
