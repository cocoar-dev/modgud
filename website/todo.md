# Roadmap

Tracking page for outstanding implementation tasks, ordered by priority.

---

## Next Up — Realm Hard-Delete

> Currently only soft-delete (deactivation). The tenant database is not dropped.

### Challenges

This is a complex feature because multiple subsystems need to be coordinated:

1. **Wolverine Durability Agent** — The async daemon per tenant must be cleanly shut down before the database is removed
2. **Marten Tenant Registry** — The tenant must be removed from `realms.mt_tenant_databases`
3. **Pending Messages/Events** — Wolverine queue entries for the realm must be drained or discarded
4. **RealmCache** — Invalidate cache so no new requests are routed to the deleted realm
5. **Active Sessions** — Invalidate sessions for the realm (cookie path `/{slug}` becomes invalid)
6. **PostgreSQL `DROP DATABASE`** — Only after everything else is completed
7. **Idempotency** — Must be crash-safe (what if the server crashes during deletion?)

### Open Research Questions

- [ ] Does Wolverine have an API for removing a tenant at runtime?
- [ ] Does Marten have a `RemoveDatabaseRecord` API (counterpart to `AddDatabaseRecordAsync`)?
- [ ] Must the async daemon for the tenant be stopped before the database is dropped?
- [ ] Should this run as a Wolverine background job (Saga?) or synchronously?
- [ ] Confirmation flow: Admin types realm slug (like GitHub repo deletion)

### Implementation (after research)

- [ ] Research: Wolverine/Marten tenant-removal APIs
- [ ] Admin endpoint: `DELETE /api/admin/realms/{slug}/permanent` with confirmation field
- [ ] Background job or synchronous implementation (depends on research)
- [ ] Cleanup order: Cache → Sessions → Wolverine → Marten Registry → DROP DATABASE
- [ ] Tests
- [ ] Documentation: [User Guide > Managing Realms](/user-guide/realms#deleting-a-realm)
