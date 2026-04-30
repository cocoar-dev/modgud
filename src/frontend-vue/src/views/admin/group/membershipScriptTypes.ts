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
]

export const membershipPreamble =
  'export {}; const __example: (p: Principal) => boolean = '
