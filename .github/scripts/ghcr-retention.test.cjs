// Unit tests of the GHCR retention plan. Run: node --test .github/scripts/ghcr-retention.test.cjs
'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const { planRetention, childDigests } = require('./ghcr-retention.cjs');

const NOW = Date.parse('2026-11-05T00:00:00Z');
const daysAgo = d => new Date(NOW - d * 86400000).toISOString();
const hex = c => c.repeat(64);
const digest = c => `sha256:${hex(c)}`;
let nextId = 1;
const version = (name, ageDays, tags = []) =>
  ({ id: nextId++, name, created_at: daysAgo(ageDays), metadata: { container: { tags } } });

const options = (children, extra = {}) => ({
  now: NOW, agedDays: 30, untaggedDays: 7, keepNewest: 3,
  childrenOf: d => children[d], ...extra,
});
const deletedNames = plan => plan.delete.map(e => e.version.name).sort();

test('the platform images and attestations of an old release are kept with it', () => {
  // The bug: 0.20.1's amd64/arm64/attestation manifests are untagged and were
  // deleted after 7 days, leaving a tag that cannot be pulled.
  const versions = [
    version(digest('a'), 60, ['0.20.1', 'latest']),
    version(digest('b'), 60), version(digest('c'), 60), version(digest('d'), 60),
    version(digest('9'), 60),                      // orphaned layer, nobody lists it
  ];
  const plan = planRetention(versions, options({ [digest('a')]: [digest('b'), digest('c'), digest('d')] }));
  assert.deepEqual(deletedNames(plan), [digest('9')]);
});

test('cosign signatures and referrer tags of a kept image are kept', () => {
  const versions = [
    version(digest('a'), 60, ['0.19.0']),
    version(digest('b'), 60),
    version(digest('e'), 60, [`sha256-${hex('a')}.sig`]),
    version(digest('f'), 60, [`sha256-${hex('b')}.sig`]),
    version(digest('1'), 60, [`sha256-${hex('a')}`]),
    version(digest('2'), 60, [`sha256-${hex('7')}.sig`]),   // signs a deleted image
  ];
  const plan = planRetention(versions, options({ [digest('a')]: [digest('b')] }));
  assert.deepEqual(deletedNames(plan), [digest('2')]);
});

test('the three newest images are kept even when they are old', () => {
  const versions = [
    version(digest('1'), 90, ['0.21.0-beta.4']),
    version(digest('2'), 91, ['0.21.0-beta.3']),
    version(digest('3'), 92, ['0.21.0-beta.2']),
    version(digest('4'), 93, ['0.21.0-beta.1']),
    version(digest('5'), 94), // child of beta.1 only
    version(digest('6'), 94), // child of beta.4
  ];
  const children = {
    [digest('1')]: [digest('6')], [digest('2')]: [], [digest('3')]: [], [digest('4')]: [digest('5')],
  };
  const plan = planRetention(versions, options(children));
  assert.deepEqual(deletedNames(plan), [digest('4'), digest('5')].sort());
});

test('prereleases and feature pointers still age out after agedDays, untagged after untaggedDays', () => {
  const versions = [
    version(digest('1'), 1, ['0.22.0-beta.1']),
    version(digest('2'), 2, ['0.21.0']),
    version(digest('3'), 3, ['saml-federation']),
    version(digest('4'), 31, ['0.20.0-beta.1']),
    version(digest('5'), 40, ['old-feature']),
    version(digest('6'), 8),
    version(digest('7'), 6),
  ];
  const children = { [digest('1')]: [], [digest('2')]: [], [digest('3')]: [] };
  const plan = planRetention(versions, options(children));
  assert.deepEqual(deletedNames(plan), [digest('4'), digest('5'), digest('6')].sort());
});

test('a kept image whose manifest cannot be read stops the prune', () => {
  const versions = [version(digest('a'), 60, ['0.20.1']), version(digest('b'), 60)];
  assert.throws(() => planRetention(versions, options({})), /refusing to prune/);
});

test('an image that is deleted needs no readable manifest', () => {
  const versions = [
    version(digest('1'), 1, ['a']), version(digest('2'), 2, ['b']), version(digest('3'), 3, ['c']),
    version(digest('4'), 50, ['old-feature']),
  ];
  const plan = planRetention(versions, options({ [digest('1')]: [], [digest('2')]: [], [digest('3')]: [] }));
  assert.deepEqual(deletedNames(plan), [digest('4')]);
});

test('childDigests reads an index and treats a single manifest as childless', () => {
  assert.deepEqual(childDigests({ manifests: [{ digest: 'sha256:x' }, { digest: 'sha256:y' }] }),
    ['sha256:x', 'sha256:y']);
  assert.deepEqual(childDigests({ layers: [] }), []);
});
