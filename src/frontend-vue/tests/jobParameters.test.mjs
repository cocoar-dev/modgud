import assert from 'node:assert/strict'
import { test } from 'node:test'
import { seedParams } from '../src/views/admin/scheduledJobs/jobParameters.ts'

// R5 (post-merge review) — the retention job reads a saved empty age as "no age sweep"; the form
// must show it empty, not the schema default, or the next save writes the default back.

const maxAge = { Key: 'maxAgeDays', Label: 'Max. age', Type: 'Number', Default: 30, EmptyMeansNone: true }
const plain = { Key: 'retentionDays', Label: 'Retention', Type: 'Number', Default: 90 }

test('a never-configured field shows its default', () => {
  assert.deepEqual(seedParams({ ParameterSchema: [maxAge, plain], Parameters: {} }), { maxAgeDays: 30, retentionDays: 90 })
})

test('a saved empty value of an EmptyMeansNone field stays empty', () => {
  assert.equal(seedParams({ ParameterSchema: [maxAge], Parameters: { maxAgeDays: null } }).maxAgeDays, null)
})

test('a saved empty value of an ordinary field still shows the default the job falls back to', () => {
  assert.equal(seedParams({ ParameterSchema: [plain], Parameters: { retentionDays: null } }).retentionDays, 90)
})

test('a saved number is shown as saved', () => {
  assert.equal(seedParams({ ParameterSchema: [maxAge], Parameters: { maxAgeDays: 7 } }).maxAgeDays, 7)
})
