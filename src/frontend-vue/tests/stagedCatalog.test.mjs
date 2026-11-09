import assert from 'node:assert/strict'
import { test } from 'node:test'
import { readStagedCatalog } from '../src/views/admin/apps/stagedCatalog.ts'

// F7-04 — a staged rename keeps its catalog Id when the draft is reopened.
const live = [{ Id: 'perm-1', Resource: 'policy', Action: 'read' }]

test('a staged rename keeps the live id and its original key', () => {
  const [row] = readStagedCatalog([{ Id: 'perm-1', Resource: 'policy', Action: 'view' }], live)
  assert.equal(row.id, 'perm-1')
  assert.equal(row.originalKey, 'policy:read')
})

test('an unchanged entry without a staged id is matched by resource:action', () => {
  const [row] = readStagedCatalog([{ Resource: 'policy', Action: 'read' }], live)
  assert.equal(row.id, 'perm-1')
})

test('a new entry has no id and no original key', () => {
  const [row] = readStagedCatalog([{ Resource: 'ledger', Action: 'write' }], live)
  assert.equal(row.id, null)
  assert.equal(row.originalKey, null)
})
