# Cocoar.Auth.Authentication

Vertical Slice für das Authentication-Kernsystem. Eigenes C#-Projekt
(`src/dotnet/Cocoar.Auth.Authentication/`), als Kopie von TimeToDos
gleichnamigem Slice in cocoar.auth eingezogen und für IdP-Bedürfnisse
erweitert.

## Was im Slice ist

- **ASP.NET Core Identity Integration** — `ApplicationUser`,
  `EventSourcedUserStore` (mehrere Identity-Interfaces), `AppSignInManager`;
  Passwortspeicherung, Account-Lockout (5 Versuche → 1 Minute Sperre)
- **Alle Login-Wege** — Password, TOTP, Email OTP, Passkey (FIDO2/WebAuthn),
  Magic Link (Self-Service + Admin-Send)
- **OIDC / Federated Login** — Dynamische Scheme-Registration zur Laufzeit,
  `UserUpdateScript` (Jint-Runtime, Claim-Mapping auf User-Felder),
  JIT-User-Erstellung, `ExternalIdentityLink`-Tracking, Flavor-Registry
  (Entra ID, Generic OIDC)
- **2FA-Enforcement-Middleware** — `TwoFactorEnforcementMiddleware`
  erzwingt das in `IAuthSettings.AuthenticationMinimumLevel` konfigurierte
  Level mit konfigurierbarer Grace-Period und per-User-Override
  (`TwoFactorExempt`)
- **Profile Self-Service** — `UserChangeRequest` mit optionalem Email
  Double-Opt-In und Admin-Approval. `Optional<T>`-Merge-Logik verhindert
  Silent-Overwrites bei mehreren parallelen Edits
- **Sessions mit Device-Tracking** — UAParser-basierte Session-Erfassung
  (Browser, OS, Gerätetyp, IP), Self-Service Revoke einzelner Sessions,
  "Logout überall" und Admin-Force-Logout
- **GDPR-Self-Service** — Daten-Export (Article 20), Account-Löschung mit
  Confirmation-Token + Cancel-Möglichkeit, Marten Data-Masking +
  ArchiveStream für Compliance
- **Recovery CLI** — Break-Glass-Subcommand
  (`recover list/reset-2fa/set-email/magic-link/rebuild-projections`)
  für Lock-Out-Szenarien
- **Admin-Endpoints** — Grace-Period-Verwaltung, Magic-Link-Versand,
  Change-Request Approve/Reject, IdP-Konfiguration (CRUD + Secret-Rotation)
- **AuthLog** — Serilog-Sink (`AuthLogSink`) → `Channel<T>` →
  `AuthLogPersistenceService` → Marten; 7-Tage-Retention,
  `Auth:`-Prefix-Filter
- **Marten-Wiring** — `UseCocoarAuthAuthentication()` registriert alle
  Identity-Documents, Event-Aliase, Inline-Projections (IdpConfig,
  ExternalIdentityLink) und die abstrakte
  `PrincipalProjectionBase`-Erweiterung

## Was der Slice bewusst nicht macht

- **Kein Authorization** — Permissions, Gruppen, Rollen, Access Scripts
  liegen im Authorization-Slice
- **Kein Frontend** — Vue-Views (LoginView, ProfileView, SetupView,
  MfaSetupModal etc.) leben in `src/frontend-vue/`
- **Kein Realm-Routing** — der Authentication-Slice arbeitet stets gegen
  die aktuelle Marten-Tenant-Session. Die Tenant-Auflösung passiert in
  `RealmMiddleware` (Api-Layer) bevor Authentication-Code läuft

## Grenzlinie zu cocoar.auth

| Verantwortung | Authentication-Slice | cocoar.auth Api |
|---|---|---|
| Wer ist dieser User? | ASP.NET Identity, Passkey, OIDC | — |
| Wie heißt der User? | `ApplicationUser` (Firstname, Lastname, Email, ...) | — |
| Welche Realm? | — | `RealmMiddleware` setzt `TenantId` vor Identity-Lookup |
| Welche Permissions? | — | Authorization-Slice |
| Realm-CRUD, OAuth-Aggregate, OpenIddict-Stores | — | Im Api/Infrastructure-Layer |

`PrincipalProjectionBase` ist die natürliche Brücke: die App leitet eine
konkrete Projection ab, die User-Events des Authentication-Slices in
`Person`-Dokumente des Authorization-Slices schreibt.

## Konfigurations-Interfaces

Der Slice hängt an drei Interfaces, die der Api-Layer registriert.

| Interface | Felder | Implementiert durch |
|---|---|---|
| `IAuthSettings` | `AuthenticationMinimumLevel`, `MagicLinkSelfService`, `TwoFactorGracePeriodDays` | `AppSettings` |
| `IServerConfiguration` | `AppUrl`, `PublicUrl` | `StartUpConfiguration` |
| `IMagicLinkConfiguration` | `Enabled`, `ExpirationMinutes`, `RateLimitMinutes` | `MagicLinkConfiguration` |

In `Program.cs` werden die konkreten Settings als Singletons hinter den
Interfaces registriert, damit der Slice unabhängig vom App-Settings-Typ
bleibt:

```csharp
builder.Services.AddSingleton<IAuthSettings>(sp => sp.GetRequiredService<AppSettings>());
builder.Services.AddSingleton<IServerConfiguration>(sp => sp.GetRequiredService<StartUpConfiguration>());
builder.Services.AddSingleton<IMagicLinkConfiguration>(sp => sp.GetRequiredService<MagicLinkConfiguration>());
```

## Abhängigkeiten

| Hart | Begründung |
|---|---|
| Marten 8+ | Document-Storage (Identity, Challenges, Change-Requests, AuthLog, IdpConfig) + Event-Store |
| WolverineFx.Marten | Handler-Discovery (RecoveryCli, IdP-Event-Handler) |
| ASP.NET Core Identity | `UserManager`, `SignInManager`, `IUserStore`-Interfaces |
| Fido2NetLib | FIDO2/WebAuthn Attestation + Assertion |
| OtpNet (via Identity DefaultTokenProviders) | TOTP-Code-Generierung + Verifikation |
| Jint | JavaScript-Runtime für `UserUpdateScript` (OIDC Claim-Mapping) |
| Serilog | `AuthLogSink` implementiert `ILogEventSink` |
| Cocoar.Auth.Authorization | `PrincipalProjectionBase` für die App-spezifische Projection |

## Status

Cocoar.Auth nutzt diesen Slice produktiv. Code unter
`src/dotnet/Cocoar.Auth.Authentication/`, eingebunden per
`ProjectReference`. Wired über `UseCocoarAuthAuthentication()` in der
Marten-Konfiguration und `services.AddCocoarAuthAuthentication()` in der
DI.

## Inhaltsverzeichnis

- [Konzepte](./konzepte) — Mental-Model, Auth-Level, Cookie-Modell, AuthLog, Profile-Service
- [Login-Flows](./login-flows) — Alle Login-Methoden im Detail
- [Identity-Provider (OIDC)](./identity-providers) — Federated Login, Flavors, UserUpdateScript
- [GDPR & Sessions](./gdpr-sessions) — Self-Service, Marten-Masking, Session-Tracking
