# UserInfo Hybrid-Emission für Single-Aud-Fall

> **Status:** Geparkt 2026-05-08. Nicht implementiert. Nicht
> blockierend.
>
> **Why:** Beim Konsolidieren des
> [Permission-Modells](./permission-modell) ist die Idee aufgekommen,
> UserInfo *zusätzlich* flach zu emittieren wenn das Token nur
> einen einzigen Audience hat. Damit könnten Resource Server die
> bewusst auf die Cocoar-Helper-Lib verzichten wollen
> (OIDC-pure, Drittanbieter, Standard-Tooling) Roles direkt aus
> UserInfo lesen, ohne `CocoarAuthClaimsTransformation`. Idee
> wurde geparkt weil aktuell **kein solcher Konsument existiert**
> — alle geplanten und bestehenden Cocoar-RSes (timetodo, knowledge,
> cocoar-policy) verwenden die Lib ohnehin.

## Die Idee in Kürze

Heute (Soll-Zustand laut [Permission-Modell](./permission-modell)):
UserInfo trägt **nur** Identity (sub/email/name). Roles, Groups
und Permissions kommen ausschließlich über die Distribution-API.

Hybrid-Variante: bei Tokens mit genau einem `aud` würde UserInfo
*zusätzlich* einen flachen Authz-Block emittieren:

```json
{
  "sub": "...",
  "email": "...",
  "name": "...",
  "roles": ["Editor", "Viewer"]
}
```

Damit könnte ein Standard-RS via reiner ASP.NET-Konfiguration
arbeiten:

```csharp
options.TokenValidationParameters.RoleClaimType = "roles";
options.GetClaimsFromUserInfoEndpoint = true;
// [Authorize(Roles="Editor")] just works — ohne Cocoar-Lib
```

Bei Multi-Aud-Tokens wird *nichts* emittiert (UserInfo bleibt
pure Identity), und der RS muss die Lib + Distribution-API nehmen.

## Wer würde davon profitieren

Der hypothetische Konsument der heute nicht existiert:

- Drittanbieter-Service der Cocoar-Auth als IdP nutzt aber keine
  Cocoar-internen Helper installieren will
- Standard-OIDC-Tool das Roles aus UserInfo erwartet
  (Admin-Dashboards, Off-the-Shelf-Software)
- White-Label-Deployment einer App die ohne Lib auskommen soll

Aktuell: niemand auf der Roadmap. Sister-Projekte (timetodo,
knowledge) und geplante interne Apps (cocoar-policy) verwenden
die Lib alle.

## Was es löst

- **Role-Gating ohne Lib** wird möglich. `[Authorize(Roles="…")]`
  funktioniert via Standard-ASP.NET-Konfiguration.
- **Reduzierte Lib-Zwang-Kopplung** für simple RSes. Ein Service
  der wirklich nur Auth + Roles braucht muss nicht durch das
  Distribution-Client-Cache-RS-Credentials-Setup gehen.

## Was es **nicht** löst

- **Permissions mit Bypass-Tier-Semantik** brauchen den
  `PermissionEvaluator`. Native `RequireClaim("permission",
  "policy:write")` würde die `<resource>:admin`- und `realm:admin`-
  Bypasses ignorieren. Ein RS der korrekte Permission-Semantik
  will, braucht die Lib unabhängig davon ob UserInfo flach
  emittiert oder nicht.
- **Multi-Aud-FatClient-Fall** bleibt Lib-only. Der Hybrid hilft
  nur im Single-Aud-Subset.

## Warum geparkt

1. **Keine konkreten Konsumenten heute.** Optimierung für eine
   hypothetische Zielgruppe ist YAGNI.
2. **IdP-Side-Komplexität:** UserInfo bekommt zwei Code-Pfade
   (single-aud → emit, multi-aud → still pure-identity). Dazu
   Audience-Cardinality-Detection und Mapping `aud → AppSlug`.
3. **Lib-Side-Komplexität:** wenn die Lib nicht mehr darauf
   verlassen kann „Authz kommt immer aus Distribution", muss sie
   bei Single-Aud-Tokens entscheiden ob sie UserInfo-Claims
   konsumiert oder selbst Distribution-API aufruft. Zwei
   Codepfade, zwei Cache-TTLs, zwei Test-Matrizen.
4. **RS-Side-Komplexität:** ein RS der sowohl Single- als auch
   Multi-Aud-Tokens sehen kann (was er typischerweise kann, weil
   Client das entscheidet), muss auf beide Shapes vorbereitet
   sein — oder er verwendet sicherheitshalber doch wieder die
   Lib. Dann war die Hybrid-Optimierung gratis.
5. **Additiv jederzeit nachrüstbar.** Im Soll-Zustand
   (Distribution-API für alles) bricht nichts wenn UserInfo
   später *zusätzlich* flach emittieren würde — Lib + RSes die
   Distribution nutzen merken davon nix.

## Wann revisiten

- Ein konkreter RS-Konsument taucht auf der „No-Cocoar-Lib"-
  Anforderung hat. Dann bewerten ob Permission-Semantik
  (Bypass-Tiers) für ihn relevant ist:
  - Wenn nur Roles: Hybrid-Emission baut sich in ~0.5–1 Tag.
  - Wenn auch Permissions mit korrekten Bypasses: Lib ist
    weiterhin nötig → Hybrid-Emission nutzt nichts mehr → nicht
    bauen.
- Anthropic-/MCP-/Drittanbieter-Integrationen die Standard-OIDC
  voraussetzen und Roles brauchen.

## Implementations-Skizze (falls jemals dran)

### Backend-Side
1. In `AuthorizationEndpoints.UserinfoAsync`:
   - Token-`aud`-Cardinality prüfen.
   - Wenn `aud.Count == 1`:
     - Mappe Audience → `OAuthApi` → `App.Slug`.
     - Hole Roles via `permissionService.GetUserRolesAsync(userId, slug)`.
     - Emittiere flat `roles: [...]` Array.
     - Optional: Permissions ebenfalls flach emittieren (mit
       deutlichem Caveat dass Bypass-Semantik damit verloren geht).
   - Wenn `aud.Count != 1`: keine Authz-Claims emittieren
     (= Soll-Zustand laut Permission-Modell).

### Lib-Side
1. `CocoarAuthClaimsTransformation` erkennt Single-Aud-Token und
   *könnte* UserInfo-Claims als Shortcut konsumieren statt
   Distribution-API zu callen — Bandwidth-Optimierung.
   Sicherheits-Default: trotzdem Distribution callen damit
   Bypass-Semantik garantiert greift; UserInfo-Claims werden
   ignoriert. Single-Aud-Shortcut nur via opt-in-Config.

### Test-Coverage
- Single-Aud emit: Token mit einem `aud` → UserInfo enthält flat
  `roles`.
- Multi-Aud no-emit: Token mit zwei `aud` → UserInfo enthält
  *kein* `roles`-Feld.
- Aud → App resolution: unbekannte Audience → 401? Oder Skip?
  (Designentscheidung wenn implementiert.)

## Querverweis

[Permission-Modell — finaler Stand](./permission-modell)
ist das autoritative Soll-Bild. Diese Hybrid-Note ist eine
**additive, opt-in Optimierung** für einen heute nicht
existierenden Use-Case. Der Soll-Zustand bleibt: Distribution-
API als einziger Authz-Kanal.
