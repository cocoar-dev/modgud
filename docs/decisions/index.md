# Architecture decisions

Modgud records the decisions that shape it, not just their outcome. Each record states what the situation was, what was decided, and what the decision costs — including the ones that were later reversed. A decision record is history; it is not edited to match what we would decide today.

Records are numbered once and never renumbered or reused. A superseded record keeps its number and gains a pointer to the record that replaced it.

## The records

| # | Decision | Status |
|---|---|---|
| [0001](./0001-oauth-mcp-client-registration) | OAuth / MCP client registration: DCR now, CIMD next | Accepted |
| [0002](./0002-public-origin-derived) | Public origin is derived, not configured | **Superseded by [0023](./0023-public-origin-is-declared)** |
| [0003](./0003-persistence-hybrid-event-sourcing-and-documents) | Persistence: hybrid event sourcing + flat documents | Accepted |
| [0004](./0004-tenancy-database-per-realm) | Tenancy: database per realm, master/system split | Accepted |
| [0005](./0005-permission-model-catalog-rbac) | Permission model: per-app catalog, RBAC via groups, two bypass tiers | Accepted |
| [0006](./0006-identity-hub-not-federation-proxy) | Identity hub, not federation proxy | Accepted |
| [0007](./0007-access-token-format) | Access tokens: reference by default, per-client JWT opt-in | Accepted |
| [0008](./0008-cimd-client-id-metadata-documents) | CIMD — client-ID metadata documents | Accepted |
| [0009](./0009-per-client-webauthn-rp-id) | Per-client WebAuthn RP-ID | Accepted |
| [0010](./0010-native-cookieless-token-grants) | Native cookieless token grants | Accepted |
| [0011](./0011-application-tier-origin-facet) | Application tier: a soft facet within a tenant | Accepted |
| [0012](./0012-invite-code-self-registration) | Invite-code-gated passwordless self-registration | Accepted |
| [0013](./0013-pagebuilder-page-variants) | PageBuilder: named page variants and activation | Accepted |
| [0014](./0014-customization-core-before-page-builder) | Finish the customization core before the page builder | Accepted |
| [0015](./0015-positions-terminals-staffing-shared-device-model) | Positions, terminals and staffing are the shared-device model | Accepted |
| [0016](./0016-position-policy-binding-control-plane) | Policy, binding and control-plane semantics for positions | Accepted |
| [0017](./0017-staged-configuration-draft-mode) | Staged configuration (draft mode) with transactional apply | Accepted |
| [0018](./0018-registration-before-proof) | Registration before proof | Accepted |
| [0019](./0019-caller-context-and-rate-limiting) | Caller context and multi-dimensional rate limiting | Accepted |
| [0020](./0020-device-aware-login-throttling) | Device-aware login throttling | Accepted |
| [0021](./0021-back-channel-logout) | Back-channel logout | Accepted |
| [0022](./0022-two-instance-operation) | Two-instance operation | Accepted |
| [0023](./0023-public-origin-is-declared) | The public origin is declared, not derived | Accepted |
