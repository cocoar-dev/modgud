# App-Einstellungen

Globale Konfigurationen, die das Verhalten von cocoar.auth steuern. Manche sind in der UI änderbar, andere stehen ausschließlich in der Server-Konfiguration (`data/configuration.json` oder Umgebungsvariablen mit Prefix `AUTH_`).

Dein Server-Administrator passt sie an; dieser Abschnitt hilft dir zu verstehen *was sie bedeuten*.

![App-Einstellungen](/screenshots/admin-einstellungen.png)

## Anmelde-Richtlinie

### Mindest-Anmeldestufe (`AuthenticationMinimumLevel`)

Legt fest, was für Anmeldungen akzeptiert wird:

| Stufe | Name | Verhalten |
|-------|------|-----------|
| 0 | Keine | Passwort allein erlaubt — 2FA optional |
| 1 | SecureLogin *(Standard)* | Passwort allein reicht NICHT — 2FA-Methode oder Passkey Pflicht |
| 2 | Passwortlos | Passwort-Login deaktiviert — nur Passkey, Magic-Link, externer Provider |

Bei Stufe 1 und 2 zeigt cocoar.auth automatisch einen Einrichtungs-Dialog nach der ersten Passwort-Anmeldung.

### Gnadenfrist für 2FA-Einrichtung (`TwoFactorGracePeriodDays`)

Bei Stufe ≥ 1 haben User ohne 2FA eine bestimmte Anzahl Tage Zeit, eine Methode einzurichten. Standard: **14 Tage**.

- Während der Frist: Login klappt, Setup-Hinweis ist wegklickbar
- Nach Ablauf: Setup-Modal wird **blockierend**
- Magic-Link bleibt als Notausgang verfügbar (zählt selbst als zweiter Faktor)

Pro User kann ein Admin:

- Die Frist **verlängern** (frischer Start)
- Den User **dauerhaft ausnehmen** (Override, audit-protokolliert)

### Magic-Link-Selbst-Service (`MagicLinkSelfService`)

Steuert den **„Anmelde-Link per Email"-Button** auf der Login-Seite.

- **Aktiv (Default):** Jeder User kann sich selbst einen Magic-Link anfordern
- **Inaktiv:** Self-Service deaktiviert — nur Admins können Magic-Links für andere generieren

Typischer Anwendungsfall für die Deaktivierung: öffentlich erreichbare Instanzen, wo Self-Service Spam-Potenzial hätte.

## Email

### Anbieter

cocoar.auth unterstützt mehrere Mail-Wege:

| Anbieter | Wofür? |
|----------|--------|
| **SMTP** | Standard-Mailserver (Office 365, Google Workspace, eigener Mailserver) |
| **Postmark** | Transactional Email Service mit höheren Zustellungsraten |
| **InMemory** | Development — Mails landen nur im Log, kein echter Versand |

Konfiguriert per `auth-settings.json` oder `AUTH_EMAIL_*`-Env-Vars.

### Welche Mails verschickt cocoar.auth?

- **Magic-Link** — einmaliger Anmelde-Link
- **Email-OTP** — 6-stelliger Code bei 2FA per Mail
- **Passwort-Reset** — Link zum Zurücksetzen
- **Email-Verifizierung** — Bestätigung bei Email-Änderung
- **Konto-Lösch-Bestätigung** — Token-Link für GDPR-Erasure
- **Admin-Notification** bei neuer Änderungsanfrage (falls konfiguriert)
- **Optional:** „Änderungsanfrage freigegeben/abgelehnt" als Bestätigung an User

### Absender-Adresse

Definiert in `EmailSettings.FromAddress` / `FromName`. Typischerweise `noreply@deinefirma.at`. Diese Adresse muss im SPF/DKIM deiner Domain authorisiert sein, sonst landen Mails im Spam.

::: warning DMARC strict
Bei DMARC-Policy `quarantine`/`reject` muss die From-Adresse auf einer Domain sitzen, die SPF und DKIM korrekt signiert. Bei Postmark/SendGrid: deren CNAME-Records eintragen. Bei eigenem SMTP: SPF-Record auf den Mail-Server zeigen.
:::

## Öffentliche URL

In Produktion muss cocoar.auth wissen, unter welcher URL die App öffentlich erreichbar ist. Wichtig für:

- Absolute Links in Emails (Magic-Link, Reset-Link)
- Passkey/WebAuthn — die Domain ist Teil der Krypto-Verifizierung
- OIDC-Discovery (`/.well-known/openid-configuration` muss die richtigen URLs enthalten)
- Redirect-URIs der OAuth-Clients

Hinter Reverse-Proxy (nginx, Sophos, Traefik): die **öffentliche** URL eintragen, nicht die interne Container-URL.

## Sitzungs-Cookies

- **Ohne „Angemeldet bleiben":** Session-Cookie (Browser zu = abgemeldet)
- **Mit „Angemeldet bleiben":** Persistent, 30 Tage, **Sliding Renewal** (jede Aktivität verlängert)
- **Nach Passkey- oder Magic-Link-Login:** immer persistent (30 Tage)

Cookies sind:

- `HttpOnly` — kein JS-Zugriff
- `Secure` — nur HTTPS (außer auf localhost)
- `SameSite=Lax` — CSRF-Schutz mit OAuth-Kompatibilität

Diese Werte sind Konvention, nicht UI-konfigurierbar.

## Datenschutz / DSGVO-Settings

- **Aufbewahrungsdauer Anmelde-Log** — Default 90 Tage
- **GDPR-Daten-Export aktiv** — kann pro Realm de-/aktiviert werden
- **Self-Service Konto-Löschung aktiv** — kann pro Realm de-/aktiviert werden
- **Cool-down-Zeit für Konto-Löschung** — Default 24h zwischen Bestätigung und endgültigem Vollzug

## Datenbankschema & Projektionen

cocoar.auth nutzt Event-Sourcing — alle Änderungen werden als Events gespeichert, abgeleitete Views aus den Events aufgebaut. Für sehr seltene Inkonsistenzen gibt es zwei Werkzeuge:

- **Konsistenz-Check** — prüft ob die Views mit dem Event-Stream übereinstimmen
- **Projektion neu aufbauen** — löscht Views und baut sie aus den Events neu auf

Beide sind ungefährlich (Source-of-Truth sind die Events) und laufen im Hintergrund. Im Normalbetrieb ist hier nichts zu tun.

Komplette Rebuild aus der Shell: siehe [Notfall-Recovery](./notfall-recovery#rebuild-projections-marten-projektionen-neu-aufbauen).

## Realm-Standard-Einstellungen

Pro Realm (siehe [Realms](./realms)) können einzelne Settings überschrieben werden — z.B. unterschiedliche Mindest-Anmeldestufe für Production-Realm vs. Demo-Realm.

Konfiguration läuft über `auth-settings.<realm-slug>.json`-Dateien oder Realm-spezifische Env-Var-Prefixes (`AUTH_REALM_<slug>_*`).
