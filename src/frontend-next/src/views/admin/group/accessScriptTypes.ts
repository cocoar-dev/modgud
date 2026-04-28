// Monaco preamble + example data for access policy script editors.
// Domain types come from /api/script-types/principal (generated from C#).
// See useScriptTypes.ts for the runtime-fetched block.

// Script format: plain body, no function wrapper.
// C# prepends `import * as env from 'env'` and appends `setResult(todos)` at execution time.
// Users write only their filtering logic; `todos`/`customers`, `user`, and `env` are globals.
// Calling `setResult(...)` explicitly is supported for early exit.

export function getDefaultScript(resourceType: string): string {
  return defaultScripts[resourceType] ?? defaultScripts['todo']
}

const defaultScripts: Record<string, string> = {
  todo: "todos = todos.where(t => t.Responsibles.some(r => r.Id === user.Id || user.GroupIds.includes(r.Id)));",
  customer: "customers = customers.where(c => c.IsImportant);",
}

// Preamble (hidden first line): declares resource-specific globals for Monaco IntelliSense.
// setResult is typed per resource so passing the wrong IQueryable<T> is a compile error.
const preambles: Record<string, string> = {
  todo: "declare const todos: IQueryable<TodoView>; declare function setResult(result: IQueryable<TodoView>): void; export {};",
  customer: "declare const customers: IQueryable<CustomerView>; declare function setResult(result: IQueryable<CustomerView>): void; export {};",
}

export function getPreamble(resourceType: string): string {
  return preambles[resourceType] ?? preambles['todo']
}

// Placeholder shown when the editor content matches the default script.
const placeholders: Record<string, string> = {
  todo: "todos = todos.where(t => t.Responsibles.some(r => r.Id === user.Id || user.GroupIds.includes(r.Id)));",
  customer: "customers = customers.where(c => c.IsImportant);",
}

export function getPlaceholder(resourceType: string): string {
  return placeholders[resourceType] ?? "// filter here"
}

export interface AccessScriptExample {
  description: string
  code: string
}

const examples: Record<string, AccessScriptExample[]> = {
  todo: [
    {
      description: "Eigene Aufgaben (Verantwortlicher direkt oder über Gruppe)",
      code: "todos = todos.where(t => t.Responsibles.some(r => r.Id === user.Id || user.GroupIds.includes(r.Id)));",
    },
    {
      description: "Selbst erstellt",
      code: "todos = todos.where(t => t.CreatedBy != null && t.CreatedBy.Id === user.Id);",
    },
    {
      description: "Verantwortlicher oder Ersteller (kombiniert)",
      code: [
        "todos = todos.where(t =>",
        "  t.Responsibles.some(r => r.Id === user.Id || user.GroupIds.includes(r.Id))",
        "  || (t.CreatedBy != null && t.CreatedBy.Id === user.Id)",
        ");",
      ].join('\n'),
    },
    {
      description: "Nur Aufgaben von zugänglichen Kunden",
      code: [
        "const allowedIds = await env.AllowedCustomerIds();",
        "todos = todos.where(t => allowedIds.includes(t.Customer?.Id));",
      ].join('\n'),
    },
    {
      description: "Eigene Aufgaben + alle Aufgaben zugänglicher Kunden",
      code: [
        "const allowedIds = await env.AllowedCustomerIds();",
        "todos = todos.where(t =>",
        "  t.Responsibles.some(r => r.Id === user.Id || user.GroupIds.includes(r.Id))",
        "  || allowedIds.includes(t.Customer?.Id)",
        ");",
      ].join('\n'),
    },
    {
      description: "Aufgaben eines bestimmten Kunden",
      code: "todos = todos.where(t => t.Customer?.Id === linq.guid('...'));",
    },
    {
      description: "Nur kritische Aufgaben",
      code: "todos = todos.where(t => t.IsCritical);",
    },
  ],
  customer: [
    {
      description: "Nur wichtige Kunden",
      code: "customers = customers.where(c => c.IsImportant);",
    },
    {
      description: "Bestimmte Kunden (Mehrfach-OR)",
      code: "customers = customers.where(c => c.Id === linq.guid('...') || c.Id === linq.guid('...'));",
    },
    {
      description: "Alles außer archivierten",
      code: "customers = customers.where(c => !c.IsArchived);",
    },
  ],
}

export function getExamples(resourceType: string): AccessScriptExample[] {
  return examples[resourceType] ?? []
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
