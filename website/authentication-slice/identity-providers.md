# Identity-Provider (OIDC Federated Login)

Der Slice unterstützt beliebig viele externe OIDC-Provider — Entra ID
(Microsoft), Google, Auth0, Keycloak, jeder OIDC-konforme IdP. Die
Konfiguration läuft pro Realm via `IdpConfig`-Dokument im Tenant-Store.

## Mental-Model

- Jede `IdpConfig` ist ein OIDC-Client gegen einen externen IdP
- Sie wird zur Laufzeit als ASP.NET Core Authentication-Scheme
  registriert (`DynamicOidcSchemeManager`)
- Beim Login wird der OIDC-Flow gegen diesen Scheme initiiert
- `ExternalIdentityLink` (`Issuer + Subject → UserId`) ist der einzige
  stabile Anker — niemand mappt User per E-Mail
- `UserUpdateScript` (Jint-JavaScript) mapt Claims auf User-Felder

## Flavors

`FlavorRegistry` hält alle eingebauten IdP-Vorlagen. Aktuell:

| Flavor | Datei | Besonderheit |
|---|---|---|
| `EntraIdFlavor` | `Identity/ExternalAuth/Flavors/EntraIdFlavor.cs` | Microsoft Entra ID — Tenant-spezifische Authority, `?prompt=select_account` Default |
| `GenericOidcFlavor` | `Identity/ExternalAuth/Flavors/GenericOidcFlavor.cs` | Standard OIDC — Authority + Client-ID + Secret reichen |

Ein Flavor liefert:

- Default-Werte für `Authority`, `Scopes`, `ResponseType`
- Erlaubte `FlavorConfigField`-Liste (Welche Inputs zeigt das Admin-UI?)
- Optionalen Default für das `UserUpdateScript`

Neue Flavors fügt man in `Identity/ExternalAuth/Flavors/` hinzu und
registriert sie in `Program.cs`:

```csharp
builder.Services.AddSingleton<IIdentityProviderFlavor, MyCustomFlavor>();
```

## IdpConfig-Dokument

Marten-Document im Tenant-Store. Felder:

| Feld | Bedeutung |
|---|---|
| `Id` | GUID, wird als Scheme-Name `oidc-{guid}` benutzt |
| `Name` | Display-Name im Login-UI ("Login with Acme SSO") |
| `Flavor` | `entra-id` / `generic-oidc` / ... |
| `Authority` | OIDC Issuer URL |
| `ClientId` | OIDC Client-ID |
| `Scopes` | Array (z.B. `["openid", "email", "profile"]`) |
| `UserUpdateScript` | JavaScript-Snippet (Jint) |
| `StoreRawClaims` | bool — wenn true, jede Login speichert die rohen Claims auf dem Link (Debug) |
| `IsActive` | bool — inaktive Provider zeigen kein Login-Button |
| `IsDeleted` | bool — Soft-Delete |

Das **Client-Secret** liegt nicht im Document, sondern in einem
separaten `IdpSecretStore` (Marten Document, getrennte Tabelle). So
landet das Secret nicht in Event-Streams oder Audit-Logs.

## Dynamische Scheme-Registration

ASP.NET Core's `AuthenticationOptions` ist normalerweise statisch — alle
Schemes müssen beim Boot bekannt sein. Wir wollen aber Realm-eigene
IdpConfigs zur Runtime hinzufügen können.

Lösung:

1. Beim Boot wird ein **Placeholder-Scheme** registriert
   (`DynamicOidcSchemeManager.SchemeNamePrefix + "placeholder"`), der
   den `OpenIdConnectHandler`-Typ und die Options-Plumbing einhängt.
   Das Placeholder-Scheme empfängt nie echten Traffic.

2. `OidcSchemeBootstrap` (HostedService) lädt beim Start alle
   `IdpConfig`-Dokumente jedes aktiven Realms und ruft
   `DynamicOidcSchemeManager.Register(idpConfig)` für jeden auf.

3. `IdpConfigEventHandlers` (Wolverine-Handler) reagieren auf
   Create/Update/Delete-Events und rufen
   `DynamicOidcSchemeManager.Register/Reload/Unregister`.

4. Der `DynamicOidcSchemeManager` registriert pro `IdpConfig` einen
   eigenen OIDC-Scheme `oidc-{guid}` mit den Optionen aus dem Document.

## UserUpdateScript

Jeder IdP liefert andere Claim-Strukturen. Wir mappen sie via
JavaScript-Snippet, ausgeführt in `Jint`.

Das Script bekommt zwei Argumente:

```javascript
// claims: Dictionary<string, string[]> — alles was im OIDC-Token kam
// user: { firstname, lastname, email, acronym, accountName } — der aktuelle User-Snapshot

return {
  firstname: claims['given_name']?.[0] ?? user.firstname,
  lastname:  claims['family_name']?.[0] ?? user.lastname,
  email:     claims['email']?.[0] ?? user.email,
  acronym:   (claims['given_name']?.[0]?.[0] ?? '') +
             (claims['family_name']?.[0]?.[0] ?? '')
};
```

Der zurückgegebene Patch wird auf den User angewendet (nur die Felder
die zurückkommen — `acronym` skippen ist OK). Felder die nicht gesetzt
werden, bleiben unverändert.

Das Test-Endpoint (`/api/admin/idp-config/{id}/test-script`) lässt
Admins das Script vor dem Deploy mit synthetischen Claims durchspielen.

::: warning Script-Fehler blockieren Login NICHT
Wenn das Script wirft, wird die Exception in `LastScriptError` auf dem
`ExternalIdentityLink` gespeichert, aber der Login geht durch — die
existierenden User-Felder bleiben einfach unverändert. Admin sieht den
Fehler im IdP-Config-Detail. Das verhindert dass ein Bug im Script alle
SSO-User aussperrt.
:::

## ExternalIdentityLink

Marten-Document das `(Issuer, Subject) → UserId` mapt. Der einzige
stabile Anker für SSO. Felder:

| Feld | Bedeutung |
|---|---|
| `Id` | hash(Issuer + Subject) |
| `Issuer` | Aus `iss`-Claim |
| `Subject` | Aus `sub`-Claim |
| `UserId` | Verlinkter Cocoar.Auth-User |
| `IdpConfigId` | Welche `IdpConfig` hat den Link erzeugt |
| `LinkedAt` | Erste Verknüpfung |
| `LastLoginAt` | Letzter Login über diesen Link |
| `LastScriptOutput` | Patch den der letzte Script-Run ausgegeben hat |
| `LastScriptError` | Exception-Message des letzten Script-Runs |
| `LastRawClaims` | Raw-Claim-Dict des letzten Logins (nur wenn `StoreRawClaims` true) |

`LastScriptOutput`, `LastScriptError`, `LastRawClaims` sind
**Debug-Artefakte** — werden bei jedem Login überschrieben, nicht
historisiert.

## Email-Konflikt-Handling

Wenn ein OIDC-Login eine E-Mail mitbringt, die schon einem anderen User
gehört (oder der gleichen UserId aber einer anderen Identity), wirft
der Processor `Idp.EmailConflict` und der Login schlägt fehl. Auf keinen
Fall implizit Accounts mergen — das ist ein Account-Takeover-Vector.
Admin muss manuell die Verknüpfung lösen (Link am alten User entfernen
oder den neuen IdP als zusätzlichen Login anhängen).

## JIT-User-Erstellung

Wenn ein OIDC-Login keine bestehende ExternalIdentityLink findet:

1. Aus den Claims wird ein `UserName` generiert (E-Mail oder
   `preferred_username`)
2. Neuer User wird angelegt, ohne Passwort, ohne 2FA-Pflicht
   (`TwoFactorExempt = false`, sondert sich ggf. später ein 2FA aus)
3. `UserUpdateScript` wird ausgeführt um die Initial-Felder zu setzen
4. `ExternalIdentityLink` wird angelegt
5. Login-Cookie wird gesetzt

Der neue User landet in keiner Gruppe → bekommt keine
Permissions außer den `app:admin`-Bypass nicht hat. Der Admin muss ihn
manuell in Gruppen aufnehmen, damit er Berechtigung hat. Auto-Membership
(siehe Authorization-Slice) kann das automatisieren.

## Account-Linking (Self-Service)

Eingeloggte User können einen weiteren OIDC-Provider mit ihrem Account
verknüpfen:

```http
POST /api/account/external-link/{idpConfigId}/start?returnUrl=/profile
```

Browser geht durch den OIDC-Flow, kommt zurück, der Processor erkennt
den eingeloggten User und legt einen `ExternalIdentityLink` an statt
einen neuen User zu erzeugen.

Unlink:

```http
DELETE /api/account/external-link/{linkId}
```
