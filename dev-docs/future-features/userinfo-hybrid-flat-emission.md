# UserInfo Hybrid-Emission — integriert in das Hauptmodell

> **Status:** Die hier skizzierte **flache Single-Audience-Emission
> wurde verworfen**, nicht gebaut. Geshipped ist die **nested
> per-Audience**-Variante: UserInfo emittiert
> `resource_access[<aud>]`-Blocks mit `permissions`/`roles`,
> per-Scope gegated, Bypass-Tiers vom IdP vor-expandiert. `groups`
> wird **nicht** emittiert (IdP-internal). Siehe
> [Permission-Modell](./permission-modell) §5 für die finale
> Architektur. Diese Note bleibt nur als Designgeschichte bestehen.

## Ursprüngliche Idee (überholt)

Beim Konsolidieren des Permission-Modells gab es kurzzeitig eine
„Pure Distribution-API"-Position, in der UserInfo nur Identity tragen
sollte. Diese Note hat überlegt ob UserInfo *zusätzlich* flach Roles
emittieren könnte für Lib-less-Konsumenten — als opt-in-Optimierung.

## Was am Ende gilt

UserInfo trägt **standardmäßig** Roles + Permissions (Groups bleiben
IdP-internal und werden nicht emittiert), nicht nur als optionale
Hybrid-Emission. Der Audience-Key macht
RS-Filterung sauber, die Bypass-Pre-Expansion macht
Lib-less-Konsumenten zur First-Class-Option. Die Annahme dass
„Distribution-API der einzige Authz-Kanal" sein müsse hat sich nicht
gehalten — sie scheiterte an SPAs ohne BFF und an Standard-OIDC-
Tooling-Erwartungen.

Konkrete Konsumenten-Szenarien (SPA mit/ohne BFF, .NET-RS,
Non-.NET-RS) sind in der [Hauptnote](./permission-modell#6-konsumenten-sicht)
beschrieben.
