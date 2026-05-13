# HA / Multi-Instance Readiness

> **Status:** Roadmap-Item. Designspace captured 2026-05-13.
> **Why:** Cocoar.Auth ist heute hart auf Single-Instance verdrahtet.
> Zwei Replicas hinter einem Load-Balancer brechen mindestens in 4
> getrennten Stellen. Audit-Finding aus
> [production-readiness-audit-2026-05-13](./production-readiness-audit-2026-05-13)
> Punkt #2.

## Was heute bricht bei 2+ Instanzen

### 1. DataProtection-Keys

`Grep AddDataProtection` über `Program.cs` → **0 Treffer.** Bedeutet
ASP.NET Core nutzt den Default: Keys werden pro Prozess in
`%LOCALAPPDATA%/asp.net/DataProtection-Keys/` (Windows) bzw.
`~/.aspnet/DataProtection-Keys/` (Linux) gespeichert, isoliert pro
Pod. Konsequenzen:

- **Auth-Cookies** brechen bei Pod-Wechsel. User loggt sich auf Pod-A
  ein, Request landet auf Pod-B, Cookie nicht entschlüsselbar →
  Redirect zu Login.
- **Antiforgery-Tokens** brechen genauso.
- Bei Pod-Restart sind alle Sessions im Pod weg.

**Fix:** Keys persistent + shared. Optionen:

- **A — Marten/Postgres** als Key-Store. Custom `IXmlRepository` der
  in der Master-DB ein Keys-Document hält. Kein extra Service nötig.
  Empfohlen, weil DB bereits da ist.
- **B — Filesystem-Mount** (z.B. NFS/EFS-Volume). Funktioniert, aber
  hängt am Mount.
- **C — Azure Key Vault / AWS KMS** als Encryption-At-Rest für die
  Keys. Overkill für Phase 1.

Plus: Keys MÜSSEN `ProtectKeysWithCertificate(signingCert)`-protected
sein, sonst kann jeder mit DB-Lesezugriff Cookies fälschen.

### 2. In-Memory Rate-Limiter

- `DcrRateLimiter` — siehe `Authorization/OAuth/Dcr/`
- `RegistrationRateLimiter` (self-reg)
- ASP.NET-Identity-Lockout (User-Lockout liegt in Postgres → safe;
  aber Per-IP-Counters in Memory)

Mit 2 Instanzen halbieren sich Rate-Limits silent. Ein Attacker spamm
mit 2x Throughput durch Round-Robin-Load-Balancing.

**Fix:** Distributed Counter. Optionen:

- **A — Marten Counter-Document** mit optimistic-concurrency. Atomic
  increment per `session.UpdateExpectedVersion`. Slow under contention
  aber funktioniert für unsere Volumina.
- **B — Redis-`INCR`** + TTL. Industrie-Standard, extra-Service.
- **C — Postgres `UPDATE … SET count = count + 1 WHERE …
  RETURNING count`** direkt — sehr robust, performant.

Empfehlung: **C** für Phase 1 (kein Redis-Zwang), **B** wenn wir
sowieso schon Redis im Stack haben.

### 3. RealmCache + andere In-Memory-Caches

`RealmMiddleware` hat einen In-Memory-Cache für die Hostname→Realm-
Auflösung. Bei 2 Instanzen ist der Cache pro-Pod aufgewärmt → erste
Requests langsam, aber sonst kein Korrektheitsbug.

ABER: Cache-Invalidation bei Realm-Settings-Änderung („Hostname
gewechselt") propagiert nicht. Pod-A weiß Bescheid, Pod-B liefert
noch alte Daten bis TTL abläuft.

**Fix:** `IDistributedCache`-Interface dazwischenschalten, default
`MemoryCache`-Backend, Multi-Instance dann via SignalR-Pub/Sub für
Invalidations oder Redis als Backend.

### 4. OpenIddict Token-Stores

OpenIddict-Stores sind in Marten → safe. Token-Revocation propagiert
über DB-Round-Trip. Kein HA-Issue, nur Latency-Note (Caching wäre
nett, ist aber nicht broken).

### 5. SignalR

`@cocoar/signalarrr` — Default-In-Memory-Backplane. Mit 2 Instanzen
sehen Subscriber auf Pod-A keine Events die auf Pod-B emittiert
wurden.

**Fix:** SignalR-Backplane. Optionen:

- Redis-Backplane (`Microsoft.AspNetCore.SignalR.StackExchangeRedis`)
- Azure SignalR Service
- Postgres LISTEN/NOTIFY (custom Backplane) — funktioniert, ist aber
  Eigenbau

### 6. Wolverine

Wolverine-Outbox liegt in Marten → safe per-Tenant. Mit 2 Instanzen:
Outbox-Worker konkurrieren um Messages. Wolverine hat Leader-Election
+ Sticky-Tenant-Routing eingebaut — muss aber konfiguriert sein.

**Aktion:** Verifizieren dass `UseDurableLocalQueues` + Sticky-Routing
aktiv sind und das Setup multi-instance-tauglich ist.

### 7. Sessions / UserSecurityData

Liegt in Tenant-DB → safe. Cookie-Re-Validation läuft alle 5min
(SecurityStampValidator) → safe.

## Migration-Reihenfolge (Phasen)

Nicht alles auf einmal. Empfohlene Reihenfolge:

1. **DataProtection persistent** (#1) — billigster Single-Point-Fix,
   gewinnt Cookie-Survivability über Pod-Restart auch ohne 2.
   Instanz.
2. **Rate-Limiter distributed** (#2) — Postgres-Counter-Pattern.
3. **RealmCache invalidation** (#3) — `IDistributedCache`-Abstraktion
   einziehen, MemoryCache-Backend default.
4. **Wolverine Multi-Instance-Setup verifizieren** (#6) — Doc-Review
   + Test.
5. **SignalR-Backplane** (#5) — erst nötig wenn 2. Instanz wirklich
   läuft.

## Was wir bewusst NICHT machen

- **Kein Redis-Mandate.** Solange Postgres die meisten verteilten
  Probleme löst (Outbox, Counters, KeyStore, DataProtection), keine
  zweite stateful Dependency dazu.
- **Kein Kubernetes-Lock-In.** Patterns müssen auf bare-VM-Pair
  funktionieren (Hetzner, OVH). Multi-Region kommt evtl. später als
  separates Thema.

## Effort

- Phase 1 (DataProtection persistent): **0.5 Tage**
- Phase 2 (Distributed Rate-Limiter): **1 Tag**
- Phase 3 (IDistributedCache-Abstraktion + Invalidation): **1 Tag**
- Phase 4 (Wolverine-Verify + Test mit 2 Instanzen lokal): **1 Tag**
- Phase 5 (SignalR-Backplane): **0.5 Tage**
- **Total: ~4 Tage** für sauberes 2-Instanz-Setup.

## Trigger

Geplant **vor erstem Hetzner-Box-Pair-Deployment** oder **vor erstem
Customer der HA-SLA fordert**, was immer früher kommt.

Anti-Trigger: keine Premature-Optimization. Eine Instanz tut's solange
die nicht überlastet ist; OpenTelemetry-Metrics (siehe
[observability-opentelemetry](./observability-opentelemetry)) sagen
uns wann das wirklich ansteht.
