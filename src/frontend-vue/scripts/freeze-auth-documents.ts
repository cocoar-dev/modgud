/*
 * One-shot capture of the starting auth documents. ALREADY RUN — and it can no
 * longer run: it imports `createAuthPageDocument()`, which Page Builder 3.0
 * removed. It was executed once against 2.20.0-beta.19 and is kept only as the
 * provenance record for src/page-builder/documents/*.json. Maintain that JSON
 * directly; do not try to regenerate it.
 *
 * Page Builder 3.0 removes `createAuthPageDocument()` — the package ships
 * nothing auth-specific any more, so the IdP owns its starting documents. Ours
 * were the upstream preset plus Modgud's alignment patches, and re-authoring
 * them by hand would silently change what a realm gets on "create page".
 *
 * So capture instead: run the existing derivation against the installed
 * pre-3.0 package and freeze the result as JSON. What ships stays byte-for-byte
 * what shipped before, and the JSON becomes the thing we maintain.
 *
 * Run once, before the 3.0 bump:
 *   node --experimental-strip-types scripts/freeze-auth-documents.ts
 */
import { mkdirSync, writeFileSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { createDefaultAuthPageSchema } from '../src/page-builder/authPageDocuments.ts'
import { AUTH_PAGE_SLOTS } from '../src/page-builder/authPageSlots.ts'

let migratedConditions = 0
let migratedRepeats = 0

/**
 * `visibleWhen: { source: 'state' }` meant the host's view state, which 3.0
 * removed as a duplicate of `runtimeContext`. Page State bindings keep the
 * name — only conditions move, and only to the context path the host already
 * passes. This is NOT one of the automatic ingest migrations.
 */
function migrateCondition(condition: Record<string, unknown>): void {
  for (const key of ['all', 'any'] as const) {
    const branch = condition[key]
    if (Array.isArray(branch)) branch.forEach(entry => migrateCondition(entry as Record<string, unknown>))
  }
  if (condition.source === 'state') {
    condition.source = 'context'
    condition.path = 'runtime.viewState'
    migratedConditions++
  }
}

function migrateNode(node: Record<string, unknown>): void {
  if (node.visibleWhen) migrateCondition(node.visibleWhen as Record<string, unknown>)

  // schemaVersion 6: repeat reads its array from contextPath.
  const props = node.props as Record<string, unknown> | undefined
  if (node.type === 'repeat' && props && typeof props.source === 'string') {
    props.contextPath = props.source
    delete props.source
    migratedRepeats++
  }

  const children = node.children
  if (Array.isArray(children)) children.forEach(child => migrateNode(child as Record<string, unknown>))
}

const here = dirname(fileURLToPath(import.meta.url))
const target = join(here, '..', 'src', 'page-builder', 'documents')
mkdirSync(target, { recursive: true })

for (const slot of AUTH_PAGE_SLOTS) {
  const document = createDefaultAuthPageSchema(slot) as unknown as Record<string, unknown>
  migrateNode(document)
  document.schemaVersion = 6

  const file = join(target, `${slot}.json`)
  writeFileSync(file, `${JSON.stringify(document, null, 2)}\n`, 'utf8')
  const nodes = JSON.stringify(document).match(/"type":/g)?.length ?? 0
  console.log(`${slot.padEnd(16)} -> ${nodes} nodes`)
}

console.log(`\nmigrated: ${migratedConditions} view-state conditions, ${migratedRepeats} repeat sources`)
