# UserInfo Hybrid-Emission — integriert in das Hauptmodell

> **Status:** Integriert. Siehe [Permission-Modell](./permission-modell)
> §5 für die finale Architektur. UserInfo emittiert **immer**
> per-Audience-nested Blocks mit `permissions`/`roles`/`groups`,
> Bypass-Tiers vom IdP vor-expandiert. Diese Note bleibt nur als
> Designgeschichte bestehen.

## Ursprüngliche Idee (überholt)

Beim Konsolidieren des Permission-Modells gab es kurzzeitig eine
„Pure Distribution-API"-Position, in der UserInfo nur Identity tragen
sollte. Diese Note hat überlegt ob UserInfo *zusätzlich* flach Roles
emittieren könnte für Lib-less-Konsumenten — als opt-in-Optimierung.

## Was am Ende gilt

UserInfo trägt **standardmäßig** Roles + Permissions + Groups, nicht
nur als optionale Hybrid-Emission. Der Audience-Key macht
RS-Filterung sauber, die Bypass-Pre-Expansion macht
Lib-less-Konsumenten zur First-Class-Option. Die Annahme dass
„Distribution-API der einzige Authz-Kanal" sein müsse hat sich nicht
gehalten — sie scheiterte an SPAs ohne BFF und an Standard-OIDC-
Tooling-Erwartungen.

Konkrete Konsumenten-Szenarien (SPA mit/ohne BFF, .NET-RS,
Non-.NET-RS) sind in der [Hauptnote](./permission-modell#6-konsumenten-sicht)
beschrieben.
