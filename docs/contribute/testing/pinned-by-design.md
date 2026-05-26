# Pinned-by-design

Behaviours in Modgud that look surprising on first read but are
intentional. Each one has at least one unit test that documents it, so
an accidental change flips the test red.

> If you're touching one of these and the test starts failing, the
> change is **probably wrong** — read the entry, decide whether the
> rationale still holds, and only then update the test.

## TenantContextMiddleware silently coerces non-string `TenantId` values

**Test:** `Api/TenantContextMiddlewareTests.cs`

**Behaviour:** if `HttpContext.Items["TenantId"]` is anything other
than a `string`, the middleware silently falls back to `"system"`
instead of throwing.

**Why:** defensive. A tenant-resolution path that misbehaves should
land the request on the system tenant (which is heavily protected by
permission checks) rather than crashing the whole request pipeline. A
loud failure mode here would turn a single bad host header into a
5xx storm.

## ResourceRegistry lookup is case-sensitive

**Test:** `Resources/ResourceRegistryTests.cs`

**Behaviour:** `Has("User")` returns false even when `Has("user")`
returns true.

**Why:** permission strings are part of the wire format. Permitting
case-insensitive lookup invites permission strings to drift in casing
across services and configuration files; a single canonical form
prevents that. Identifiers in code, configuration, and the database
must agree byte-for-byte.

## GenericOidcFlavor.DeriveEndpoints does not normalise trailing slashes

**Test:** `ExternalAuth/GenericOidcFlavorTests.cs`

**Behaviour:** an authority of `https://idp.example.com/` and
`https://idp.example.com` produce different endpoint URLs (the
trailing slash carries through into the derived URLs).

**Why:** the flavor mirrors what the upstream IdP advertises in
`.well-known/openid-configuration`. Some IdPs (Keycloak among them)
disagree on the canonical form; normalising here would mask the
difference and produce surprising mismatches between the derived URLs
and the actual issuer claim. The admin who configures the IdP is
expected to enter the form that matches the issuer.

## Aggregates have no post-delete write guards

**Test:** the `*StateProjection` and `*AggregateTests` series

**Behaviour:** if a `Soft-Deleted` aggregate receives an `Updated`
event after the delete, the projection happily applies it.

**Why:** validation lives one layer up — in the `Application` layer's
`*State` inline projections and command handlers, which check
`IsDeleted` before issuing the next event. The aggregates themselves
stay event-replay-safe and dumb: replay must succeed for any
historical sequence of events the store ever wrote, regardless of
whether that sequence is still legal under today's rules. Pushing the
"may I write this?" decision into the aggregate would couple replay
correctness to evolving business rules, which makes the event store
brittle. Validation belongs at the boundary that *receives* commands,
not at the one that *replays* the past.

## Wolverine `OutboxedSessionFactory` and tenant routing

**Test:** none currently — this is a known limitation, not a pinned
behaviour.

The `TenantedSessionFactory` reads
`HttpContext.Items["TenantId"]`, which works for any session injected
into a Wolverine handler that was invoked via `IMessageBus.InvokeAsync`
(the bus copies the tenant onto the envelope). Wolverine handlers that
inject `IDocumentSession` directly outside the `InvokeAsync` chain hit
`MasterTableTenancy.Default` and crash.

Today this is harmless because every Wolverine handler in the
codebase is invoked via the bus. The day a future handler injects
`IDocumentSession` and runs outside that chain (e.g. a scheduled
background job that opens its own scope), it will need a different
tenant-resolution path. Two viable shapes when that day comes:

- Replace `BuildSessionsWith<TenantedSessionFactory>` with a deeper
  Marten/Wolverine integration that lets the
  `OutboxedSessionFactory` ask the envelope for the tenant.
- Decorate the `OutboxedSessionFactory` to consult
  `IHttpContextAccessor` directly when the envelope is empty.

Either way, the test that pins the new behaviour goes here.

## Why this list is short

Most architectural choices are documented inline in the source
(comments, XML docs) or in the slice READMEs. This page is reserved
for the cases where:

- The current behaviour looks like a bug at a glance, **and**
- A test exists explicitly to lock the behaviour rather than to
  verify a public contract, **and**
- A reasonable contributor would otherwise "fix" it.

If you're adding a pinning test, add an entry here so the next
person knows why it exists.
