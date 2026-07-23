# Audit storage ownership decision

Status: accepted for pre-1.0

F2 (tenant isolation) and F4 (erasure) are resolved as one storage decision:

1. The Control Plane is a normal realm. Its realm DB contains only its data.
2. Tenant-visible security events live in the owning realm DB; there is no
   central realm-attributed table or hidden cross-DB union.
3. True deployment events live in the Global Store using a separate,
   compile-time PII-free `PlatformAuditEvent` type.
4. Realm events use explicit actor/target/forensic fields. Free-form Actor,
   Reason, Message and generic property bags are not persisted.
   Cross-realm Control-Plane writes create correlated events: the actor and
   request metadata stay in the Control-Plane realm, while the target realm
   receives only an `ActorKind=ControlPlane` counterpart.
5. Known users are referenced by subject ID. Unknown identifiers become
   realm-specific HMAC fingerprints before persistence.
6. Account erasure removes identity profile data; short-retention forensic
   records keep pseudonymous IDs and technical context until their realm
   retention expires. A realm hard-delete removes them with the database.
7. Realm Security retention defaults to 7 days (1–365); Platform retention
   defaults to 365 days. Arbitrary clear/delete endpoints do not exist.
8. F7 assigns every streamless event type one enforced delivery class:
   Required (transactional or synchronously durable), Incident
   (synchronously durable), Abuse (bounded raw input plus retrying count
   aggregates), or Telemetry (explicitly best-effort). The complete operational
   contract is documented in [Security and platform logs](../admin/auth-log.md#delivery-guarantees).
