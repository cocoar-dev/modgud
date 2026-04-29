// Monaco preamble + example data for access policy script editors.
// Domain types come from /api/script-types/principal (generated from C#).
// See useScriptTypes.ts for the runtime-fetched block.

// Script format: plain body, no function wrapper.
// C# prepends `import * as env from 'env'` and appends `setResult(rows)` at execution time.
// Users write only their filtering logic; the resource's row variable, `user`, and `env` are globals.
// Calling `setResult(...)` explicitly is supported for early exit.

export function getDefaultScript(_resourceType: string): string {
  return ""
}

// Preamble (hidden first line): declares resource-specific globals for Monaco IntelliSense.
// setResult is typed per resource so passing the wrong IQueryable<T> is a compile error.
const preambles: Record<string, string> = {}

export function getPreamble(resourceType: string): string {
  return preambles[resourceType] ?? "export {};"
}

export function getPlaceholder(_resourceType: string): string {
  return "// filter here"
}

export interface AccessScriptExample {
  description: string
  code: string
}

export function getExamples(_resourceType: string): AccessScriptExample[] {
  return []
}

export const membershipExamples: AccessScriptExample[] = [
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
