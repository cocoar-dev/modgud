# Two-instance operation: one Wolverine-coordinated cluster, Postgres backplane, N-1 release rule

**Status:** Accepted — shipped 2026-09-05 in v0.12.0 (PR #226) · **Decided:** 2026-09-04

# Context

Modgud runs as exactly one container. Every image update is downtime. The operator's immediate pain is **the update**; a second instance also gives cheap resilience against one container crashing. "High availability" in the SLA sense (multi-box failover under load, chaos drills) stays out of scope, but the design below is the correct multi-instance design, not a rolling-update shortcut: once two instances run, they run **all the time**, both serving traffic, and everything must be correct in that steady state, not only during the update window.

This ADR supersedes the "2b" section of the design note `designs/ha-multi-instance`. That note predates ADR 0007 and still lists in-memory rate limiters; those are gone (`PostgresRateLimitStore`).

Revision notes (all 2026-09-04/05): the first draft proposed Marten `HotCold` plus a hand-written advisory-lock election for Quartz and an optional backplane; rejected as second choice. Increment 1 was built with the SignalARRR Redis backplane on Valkey plus a Redis data-event relay; SignalARRR then shipped its Postgres backplane (5.1.0-beta.1) and the default moved to Postgres. A code audit next showed that Modgud has no targeted SignalR sends at all (no `IHubContext`, no `Clients.*`, no groups) and every hub is a server stream fed by the in-process `DataEventDispatcher` observable, so the backplane routed nothing and was removed in favour of Modgud's own LISTEN/NOTIFY relay. The finding went to SignalARRR (Atlas `cluster-aware-observables-idea-2026-09`), which shipped **cluster subjects** and Postgres catch-up the same day (5.1.0-beta.2, PRs #72/#73; 5.1.0 final the same afternoon). Final shape: the Postgres backplane carries a cluster subject with Modgud's data events; the hand-written relay is gone. Driving a complete external login across the rig then surfaced that the SSRF guard had no operator exemption for identity providers on private networks; D11 adds it. The "As built" section records what the rig showed.

## What already works with two instances (state of v0.11.0)

- DataProtection keys per realm in Marten (HA-2a).
- OpenIddict stores, browser sessions, SecurityStamp, PendingRegistration (ADR 0006), session grants (ADR 0009), signing keys: all in the tenant database.
- Rate limits and login throttling: Postgres (ADR 0007/0008).
- Process-local caches with **bounded staleness**: `RealmCache`, `RealmKeyStore` (revalidation interval), CORS origins (60 s), CIMD metadata. This is a deliberate design (re-validate against the DB after seconds), not a gap.
- Wolverine already has `MainDatabaseConnectionString` (master DB) for node coordination.

## What broke before this ADR when two instances ran concurrently

1. **Wolverine `Solo`**: both nodes drain the outbox; emails and forwarded events execute twice.
2. **Marten async daemon hard-wired to `DaemonMode.Solo`**: both nodes run the async `AuthAuditView` projection, the App change-feed subscription and the back-channel-logout subscription (ADR 0009).
3. **Quartz in-memory job store**: every node runs every job; `BackChannelLogoutRetryJob` double-delivers, run history is written twice.
4. **ASP.NET `Session` on `AddDistributedMemoryCache`**: sole consumer is the passkey registration challenge; a ceremony split across nodes fails.
5. **No graceful drain**: nothing turns `/health/ready` to 503 on SIGTERM.
6. **SignalR is node-local**: `PositionHub` (terminal activation pushes), `InviteCodeHub`, inbox notifications, admin grids, `BrowserSessionConnectionRegistry` (instant disconnect on revoke). A push raised by a request on node A never reaches a connection on node B. **This is a functional break in steady state**, not a cosmetic one. Every hub stream is fed by the in-process `DataEventDispatcher` observable and there are no targeted sends, so a backplane's send routing alone carries none of these events; the observable itself has to become cluster-wide.
7. **Dynamic OIDC/SAML schemes are registered by node-local Wolverine handlers**. A provider created on node A is unknown on node B until B restarts; a challenge on B for that scheme throws. There is no lazy registration on the request path.
8. **Observability buffers** (`ObservabilityActivityBuffer`, `RealmErrorBuffer`) are process-local; the admin live view shows one node's half.
9. **Projection rebuild endpoint** pauses only the local daemon.

## Verified library facts (Marten 9.23 / JasperFx.Events 2.47 / Wolverine 6.27 / Quartz 3.18 / SignalARRR 5.1.0)

- Wolverine `MartenIntegration.UseWolverineManagedEventSubscriptionDistribution` replaces Marten's `AddAsyncDaemon(HotCold)`; Wolverine refuses the combination at start (`ManagedDistributionDaemonModeValidator`). Its `WolverineProjectionCoordinator` implements Marten's `IProjectionCoordinator`, so the existing pause/stop gate keeps working.
- Wolverine `Balanced`: leader election over the node table in the master DB with heartbeats; stale nodes are recovered and their agents/envelopes reassigned. Per-tenant-database distribution keeps all projections of one database on one node. Observed: JasperFx.Events 2.47 warns at start that several "extended progression writers" are attached per realm database (one per shard agent). Extended progression tracking is off in Modgud (no extended columns in `mt_event_progression`), so nothing is written twice; projections run exactly once per shard. Follow-up: check whether Wolverine ≥ 6.28 shares one daemon per database, else report upstream.
- Quartz.NET clustering (`UsePersistentStore` + `UseClustering`) provides cluster-wide `DisallowConcurrentExecution`, misfire handling, recovery of jobs that were executing on a dead node (`RequestRecovery`), and load spreading; needs the `QRTZ_*` schema in a shared database. `UseProperties = true` requires string-only `JobDataMap` values.
- SignalARRR backplanes route **targeted sends** (`Clients.*`, groups, user/connection addressing) and replicate connection attributes; they never see a server stream that reads from a process-local observable. Since 5.1.0 a **cluster subject** (`AddSignalARRRClusterSubject<T>("name")`, `IClusterSubject<T>`) closes that gap: an `IObservable<T>` whose events are relayed over the backplane transport to the same-named subject on every node — once locally, once remotely, never echoed, in order per publishing node, `OnNext` fire-and-forget through a per-subject outbox, `T` fixed at registration so no type name travels and an unreadable payload is dropped with a warning (N-1 safe), a plain local subject without a backplane. The Postgres backplane (`LISTEN`/`NOTIFY`, schema `signalarrr`, one listener connection per node tagged `application_name=signalarrr-backplane-listener:{nodeId}`) passes every envelope through an unlogged `messages` table via a `publish` function that serialises publishes on an advisory lock (ids in commit order), and, with catch-up on by default, a node that lost its listener replays what it missed from its cursor in id order; an outage longer than `MessageRetention` (5 min) is reported as a gap. A lost listener is logged as a warning and resubscribed within a second; the error is reserved for reconnects that keep failing. Primary only; no transaction-pooling PgBouncer for the listener; schema created idempotently on start.
- `CREATE … IF NOT EXISTS` is not atomic across sessions: two nodes booting together must serialise schema creation on an advisory lock (seen once on the rig with the hand-written relay; the library backplane does this itself).
- ASP.NET Core SignalR requires that all requests of one connection reach the same server process. Sticky sessions are therefore required at the proxy; the backplane solves fan-out, not affinity. Caddy's cookie affinity re-pins a client to the surviving node when its node is being replaced, and keeps it there afterwards.
- Modgud's SSRF guard (`SsrfSafeHttpHandlerFactory`, security review 2026-07) resolves every admin-supplied URL itself and refuses any non-public address for OIDC discovery/back-channel, SAML metadata, CIMD documents and back-channel logout delivery, in every environment but Development/Testing for the last one. It had no operator-level exemption.

# Decision

## D1. Two-instance operation is a supported, first-class deployment shape

Both instances serve traffic all the time. Rolling updates are the consequence, not the design goal. Failover HA across machines is not claimed.

## D2. One code path: Production is always cluster-capable

There is no instance-count switch. In `Production` the host always runs with Wolverine `Balanced`, Wolverine-managed projection distribution, clustered Quartz, and the SignalARRR Postgres backplane with the live-update cluster subject on the master database, also when only one container is running. `Solo`/in-memory/no backplane remain for `Development` and `Testing` only. How many nodes are alive is read from Wolverine's node table at runtime (`IClusterNodes`); a host without the relay that sees a second live node (only possible outside Production) reports **not ready**.

## D3. One coordinator: Wolverine

`IntegrateWithWolverine(o => o.UseWolverineManagedEventSubscriptionDistribution = true)` plus `DurabilityMode.Balanced` in Production. Marten's own daemon coordinator is not registered in Production. The `Wolverine__DurabilityMode` env override is gone; the environment decides. Testing keeps the explicit interactive daemon per consistency boundary.

## D4. Quartz: native clustering on a Postgres store in the master DB

`UsePersistentStore` with the Postgres provider, `UseClustering()`, System.Text.Json serialization, `SchedulerId = AUTO`, `UseProperties = true`. Tables live in schema `quartz` of the master database; `QuartzSchemaBootstrap` applies Quartz's own script once, under a cluster advisory lock, in the same startup step as the Marten master/global schema. Schedule reconciliation (`RealmJobScheduler`) runs under the same lock. Every job is `RequestRecovery`. Manual-trigger job data is string-only. Clustering needs synchronised clocks; documented.

## D5. Live updates as a cluster subject on the SignalARRR Postgres backplane

Production registers `AddSignalARRRPostgresBackplane` (master DB connection string, schema `signalarrr`, node id `{NodeName}-{guid}`) and one cluster subject, `modgud-data-events`, of type `DataEventEnvelope`. `ClusterSubjectDataEventRelay` is the only Modgud code involved: it implements the dispatcher's `IDataEventRelay` seam by handing every locally raised `DataEvent` to the subject as an envelope stamped with this node's id, and subscribes to the subject to replay peers' envelopes into the dispatcher (`DispatchRemoteEvent`, which never relays again). The subject notifies local subscribers of local events too, so the adapter skips envelopes carrying its own node id; each browser therefore receives every event exactly once, from the node its connection is pinned to. `DataEventEnvelope` is Modgud's wire contract (enums by name; payload items with their CLR type name and JSON). Payload types are resolved only from Modgud's own assemblies plus a closed list of plain value types (`string`, `Guid`, numbers, dates) — the id of a deleted entity travels as a `string`, which the first whitelist refused and the rig caught. Anything else is dropped with a warning, which keeps a mixed-build cluster safe. Delivery, ordering and catch-up after a listener drop are the backplane's. No hand-written transport, no `modgud_cluster` schema, no Redis option; nothing to configure.

Sticky sessions at the proxy stay required.

## D6. Per-node resolution instead of cross-node event propagation

Every node must be able to serve any request from the database alone:

- **OIDC/SAML schemes:** `LoginProviderSchemeMaterializer` keeps this node's schemes equal to the realm's `LoginProvider` documents. `RealmAwareAuthenticationSchemeProvider` (replacing the framework provider, materializer resolved lazily to avoid a DI cycle) calls it on `GetRequestHandlerSchemesAsync`, `GetSchemeAsync` for `Oidc_*` and `GetAllSchemesAsync`; the SAML endpoints call it before their lookup. Bounded staleness 15 s; the committing node's Wolverine handlers force an immediate refresh, which re-registers everything; disabled and soft-deleted providers are unregistered explicitly so the node converges regardless of who registered before. `LoginProviderSchemeBootstrap` warms all active realms at start (optimisation only). The former `OidcSchemeBootstrap`, `SamlSchemeBootstrap` and `SamlLoginProviderEventHandlers` are gone.
- **Passkey registration:** the web flow uses the existing `PasskeyEnrollCeremony` document with a path-scoped `Modgud.Passkey.Enroll` cookie carrying only the id; RP ID pinned at options time; single use. `AddSession`/`UseSession` and the `Modgud.Session` cookie are removed.
- **Session revocation across nodes:** no cross-node targeting. The node-local registry aborts instantly on the revoking node; every hub invocation re-checks the session row; `BrowserSessionConnectionSweeper` re-validates every idle connection's session against the database every 30 s and aborts the dead ones. DB-driven, no cross-node message.
- **Observability live view:** stays per node in increment 1 (documented); persistence is increment 2.

Bounded-staleness caches stay as they are and are documented as the propagation bound.

## D7. Graceful drain

On `ApplicationStopping`, `ShutdownState` flips; a middleware in front of the health pipeline answers `/health/ready` with 503 (no framework error log for a planned drain; `ClusterHealthCheck` keeps the same rule as a backstop); the host is held for `Cluster__DrainDelaySeconds` (default 5, Production only) before Kestrel stops; `HostOptions.ShutdownTimeout` = 30 s + drain. Wolverine deregisters its node on graceful stop, so the peer takes over immediately. Docker: `STOPSIGNAL SIGTERM`, `stop_grace_period` 45 s documented.

## D8. Projection rebuild is a single-instance operation

The endpoint refuses with 409 when `IClusterNodes` reports more than one live node.

## D9. Schema compatibility: N-1 rule, enforced in CI (increment 2)

> **Every release must run next to its predecessor (N-1) against the same databases for the duration of an update.**

Not rolling-safe (release notes: `Rolling update: stop required (reason)`): Marten bumps that replace `mt_*` functions; projection rebuilds; inline projection shape changes the old code cannot read; new event types without upcasters. The PR template carries the checkbox. The `rolling-compat` CI job (previous release image against a database migrated by the current build) is increment 2. The first update onto this release is itself a stop: a 0.11.x container (Marten Solo daemon, in-memory Quartz) cannot run beside a cluster node.

## D10. Ingress and orchestration

Caddy `lb_policy cookie` + `health_uri /health/ready` + `health_interval 5s` + `fail_duration 30s`; nginx equivalent; Compose with two named services plus an ordered update script; Swarm `order: start-first` as the native alternative. All in `docs/operate/deployment.md`, section "Running two instances".

## D11. Operator allow-list for the SSRF guard

The guard stays on for every admin-supplied URL in every environment; a realm admin cannot widen it. The platform operator declares the hosts of an identity provider or resource server on the private network deployment-wide: `OutboundHttp:AllowedPrivateHosts` (env `OutboundHttp__AllowedPrivateHosts`), exact host names or `*.suffix`, parsed once into `SsrfAllowList` and consumed by all four guarded fetchers through DI. A listed host is exempt from the address classification only — the socket still goes to the resolved address, TLS still validates the name, redirects stay off, timeouts stay tight. The refusal message names the setting. Documented under deployment ("Identity providers on private networks") and in the login-provider pitfalls. Unit tests cover parsing, matching and the guard against a loopback listener with and without the entry.

# As built and verified (2026-09-05)

Branch `feat/two-instance-adr-0010`, rebased onto develop `21de8f20` (v0.11.1); commits `48e0a0bb`, `d1059d93`, `89ed1847`, `4deec057`, `bf0447d2`, `1f6c354b`, `09cde375`, `62443e06`, `62869db1`, `f1bc501f`, `5e8d4b03`. Settings: `ClusterSettings` (`Cluster`: `DrainDelaySeconds`, `NodeName`), `OutboundHttpSettings` (`OutboundHttp`: `AllowedPrivateHosts`). `ClusterHostingOptions.CrossNodeRelay` (true in Production) drives the backplane and subject registration and the readiness rule. SignalARRR 5.1.0 on server (Server, Backplane.Postgres) and TypeScript client. Unit tests: dispatcher relay seam, envelope round trips (document payload, deleted-entity id) through the subject's serializer, cluster-subject adapter, cluster health check, connection registry snapshot, SSRF allow-list. Integration tests: database-driven scheme materialisation (four cases), cookie-based web passkey registration with a software authenticator; the ExternalAuth suite adapted to database semantics. Cross-node transport is the library's and tested there. Full suites green: 1666 unit, 809 integration (before the allow-list commit; the rerun after it is recorded in the memory file).

Local rig: two containers of the branch image, the developer's native Caddy with cookie affinity and active checks, against the local production-shaped database. For the external-login round trip: a Dex container (`mockCallback` connector — the authorize request completes with a fixed user, no login form) behind the same Caddy at `https://idp.localhost`, the Caddy local root CA appended to the containers' trust bundle via `SSL_CERT_FILE`, `idp.localhost` mapped to the host gateway, and `OutboundHttp__AllowedPrivateHosts=idp.localhost` on both nodes (compose overlay `compose.two-nodes.idp.yml`).

| Check | Result |
|---|---|
| Both nodes ready | `cluster: Healthy — 2 live nodes, backplane and live-update relay active`; `signalarrr.nodes` = 2 rows; one `LISTEN` session per node, tagged `signalarrr-backplane-listener:<node>` |
| Work distribution | Wolverine assignments spread over both nodes (per-realm projection shards + durability agents, one leader); Quartz: two scheduler instances, 35 jobs, all triggers WAITING |
| `docker kill` one node | Survivor holds every assignment and the scheduler within 60 s; dead node row gone after ~70 s |
| Node rejoins | Assignments rebalanced 10/7 within 40 s |
| `docker stop` one node | Readiness 503 "Draining" for the 5 s window, stop took 9 s, node deregistered immediately from Wolverine and from `signalarrr.nodes`, no health-check error noise |
| Rolling replacement of both nodes while probing discovery through Caddy every 250 ms | 0 failures on every build (Valkey, Postgres backplane beta.1, own relay, cluster subject beta.2/beta.3/5.1.0, allow-list); a mixed pre-#225/post-#225 cluster for 20 s during the rebase roll — a live N-1 exercise |
| Simultaneous cold start of both nodes from an empty `signalarrr` schema | both ready in 8 s, schema and `publish` function created once, 0 errors |
| Listener connections killed with `pg_terminate_backend` | resubscribed in 0.5 s, catch-up ran, readiness stayed 200; logged as a warning on 5.1.0 |
| **End-to-end live update across nodes** (browser pinned to node a via the Caddy `lb` cookie, API client pinned to node b, both logged in through magic links read from Mailpit) | App created on b appears in a's grid without reload; one `ClusterEvent` row per event in `signalarrr.messages` with node b as origin; delete on b removes the row on a — after the whitelist fix (`f1bc501f`); before it node a dropped every delete with "Payload type System.String is not a type of this deployment" |
| **Complete external OIDC login on the other node** (provider created through node b; client pinned to node a) | Without the allow-list: 500, `'idp.localhost' did not resolve to a routable public address` — the SSRF guard, working as designed. With it: challenge → Dex → `GET /signin-oidc/adr0010-idp` → token exchange → `External login (JIT-created) user …` → session on node a, `/api/account/me` = `kilgore@kilgore.trout`, `IsFederated: true`; repeated as a returning login on node a; the same flow from the browser pane ended on the dashboard |
| Node started without the relay (pre-Postgres build) | `Unhealthy — 3 nodes are alive but …` with the fix named |

Nothing in this ADR's scope is left unverified by machine. Not covered here, by design: failover across machines and the database itself.

# Increments

## Increment 1: cluster-capable core — DONE (one PR)

Everything in D2–D8, D10 and D11, docs runbook, PR-template checkbox, release-notes rule.

## Increment 2: observability persistence and CI enforcement

Persisted activity/error events with retention (D6); `rolling-compat` CI job (D9). Open follow-up outside the ADR: the JasperFx "extended progression writers" warning under Wolverine-managed distribution.

# Consequences

- Single-instance operators run the cluster code (Balanced, managed distribution, clustered Quartz, Postgres backplane + cluster subject) with a small amount of extra coordination traffic to the master DB and two extra schemas there (`quartz`, `signalarrr`); the database role needs `CREATE` once. Behaviour is otherwise unchanged.
- Two-instance operators need Postgres (primary, no transaction-pooling PgBouncer for the listener), two Modgud containers with the same OpenIddict certificates, a sticky proxy with active readiness checks, and synchronised clocks. Nothing else. Rolling-safe releases update with zero failed requests; the rest announce a short stop.
- Operators with an identity provider or resource servers on a private network list those hosts once, deployment-wide; realm admins still cannot point Modgud at internal addresses.
- Modgud gains no bespoke coordination or transport code: Wolverine coordinates, Quartz clusters, SignalARRR fans out; Modgud contributes a 70-line adapter and its own wire contract for data events.
- The ASP.NET session middleware is gone; one fewer cookie.
