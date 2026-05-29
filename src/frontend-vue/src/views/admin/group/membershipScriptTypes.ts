// Monaco examples + preamble for the membership-predicate editor.
// Domain types come from /api/script-types/principal (generated from C#).
// See useScriptTypes.ts for the runtime-fetched block.

export interface MembershipScriptExample {
  description: string
  code: string
}

export const membershipExamples: MembershipScriptExample[] = [
  {
    description: "Alle aktiven Personen",
    code: "(p) => Type.Is(p, 'person') && p.IsActive",
  },
  {
    description: "Personen mit bestimmtem Vornamen-Prefix",
    code: "(p) => Type.Is(p, 'person') && p.IsActive && p.Firstname?.startsWith('A')",
  },
  {
    description: "Service-Accounts (AccountName startet mit 'svc-')",
    code: "(p) => Type.Is(p, 'service-account') && p.AccountName?.startsWith('svc-')",
  },
  {
    description: "Nur bestimmte E-Mail-Domäne",
    code: "(p) => Type.Is(p, 'person') && p.Email?.endsWith('@example.com')",
  },
  {
    // Federation v1 (ExternallyDrivable groups only): session-scoped surface.
    // p.ExternalGroups is the current provider's groups claim (always an array);
    // p.Source is "local" or "provider:<slug>". Never written to durable members.
    description: "Federation: upstream IdP group (only for 'Externally drivable' groups)",
    code: "(p) => Type.Is(p, 'person') && p.IsActive && p.ExternalGroups.includes('entra-admins')",
  },
  {
    description: "Federation: scope a rule to one provider via p.Source",
    code: "(p) => p.Source === 'provider:acme-entra' && p.ExternalGroups.includes('finance')",
  },
]

export const membershipPreamble =
  'export {}; const __example: (p: Principal) => boolean = '
