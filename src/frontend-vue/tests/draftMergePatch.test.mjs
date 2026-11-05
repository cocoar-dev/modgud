import assert from 'node:assert/strict'
import { test } from 'node:test'
import { stagedFlag, stagedText } from '../src/views/admin/user/draftMergePatch.ts'

// AID-06 — a staged user entity is a merge patch: absent keeps live, null is a clear.

test('an absent name keeps the live value instead of becoming an empty (cleared) field', () => {
  const partialPatch = { Email: 'new@example.test' }
  assert.equal(stagedText(partialPatch, 'Firstname', 'Ada'), 'Ada')
  assert.equal(stagedText(partialPatch, 'Acronym', undefined), undefined)
})

test('an explicit null stages a clear and is shown as cleared, not as the live value', () => {
  const clearing = { Lastname: null }
  assert.equal(stagedText(clearing, 'Lastname', 'Lovelace'), '')
})

test('a staged text value wins over the live value', () => {
  assert.equal(stagedText({ Firstname: 'Grace' }, 'Firstname', 'Ada'), 'Grace')
})

test('staged active and confirmed flags are shown; absent flags keep the live value', () => {
  assert.equal(stagedFlag({ IsActive: false }, 'IsActive', true), false)
  assert.equal(stagedFlag({}, 'EmailConfirmed', true), true)
})
