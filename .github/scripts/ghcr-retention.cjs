// Decision logic of the GHCR retention workflow (cd-ghcr-retention.yml), kept
// free of I/O so it can be unit-tested (ghcr-retention.test.cjs).
//
// GHCR lists every manifest pushed to the package as a "version". A
// multi-arch release is an image INDEX (tagged, e.g. `0.20.1`) whose
// per-platform image manifests and attestation manifests are separate,
// UNTAGGED versions — the tag only resolves while those children exist.
// Pruning untagged versions by age alone therefore deletes the content of
// every tagged image after UNTAGGED_DAYS and leaves tags that cannot be
// pulled ("manifest unknown"). Children and cosign artefacts of every image
// that is kept are protected here.
//
// Version kinds:
//   image     — has at least one ordinary tag (release, prerelease, pointer)
//   reference — only tags of the form `sha256-<hex>[.sig|.att|.sbom]`
//               (cosign signature/attestation, OCI referrers fallback tag);
//               it belongs to the manifest whose digest it names
//   untagged  — no tag: a child of an index, or an orphaned layer
//
// Image keep rules (an image is kept when ANY applies):
//   - a tag is a stable release `X.Y.Z` or a long-lived pointer (latest, beta, rc)
//   - it is among the `keepNewest` most recent images
//   - it is not older than `agedDays`
// Untagged / reference versions are kept when the digest they belong to is
// protected (a kept image or one of its children); otherwise they age out
// after `untaggedDays`.

'use strict';

const KEEP_LABELS = new Set(['latest', 'beta', 'rc']);
const isRelease = t => /^\d+\.\d+\.\d+$/.test(t);
const isKeepTag = t => isRelease(t) || KEEP_LABELS.has(t);
const REFERENCE_TAG = /^sha256-([0-9a-f]{64})(\..+)?$/;

const DAY_MS = 86400000;

function classify(version) {
  const tags = version.metadata?.container?.tags ?? [];
  if (tags.length === 0) return { kind: 'untagged', tags };
  const refs = tags.map(t => REFERENCE_TAG.exec(t));
  if (refs.every(Boolean)) {
    return { kind: 'reference', tags, refersTo: refs.map(m => `sha256:${m[1]}`) };
  }
  return { kind: 'image', tags };
}

/**
 * @param {Array<{id:number,name:string,created_at:string,metadata?:object}>} versions
 *   GHCR package versions; `name` is the manifest digest.
 * @param {{ now:number, agedDays:number, untaggedDays:number, keepNewest:number,
 *           childrenOf:(digest:string)=>string[]|undefined }} options
 *   `childrenOf` returns the manifest digests an image index lists (an empty
 *   array for a single image manifest), or undefined when it could not be read.
 * @returns {{ keep: Array<{version:object, reason:string}>,
 *             delete: Array<{version:object, reason:string}> }}
 * @throws when the children of a kept image are unknown — pruning without
 *   them could delete the content of an image that is kept (fail closed).
 */
function planRetention(versions, options) {
  const { now, agedDays, untaggedDays, keepNewest, childrenOf } = options;
  const ageDays = v => (now - new Date(v.created_at).getTime()) / DAY_MS;
  const classified = versions.map(v => ({ v, ...classify(v) }));

  const images = classified
    .filter(c => c.kind === 'image')
    .sort((a, b) => new Date(b.v.created_at) - new Date(a.v.created_at));
  const newest = new Set(images.slice(0, keepNewest).map(c => c.v.id));

  const keep = [];
  const del = [];
  const protectedDigests = new Set();

  for (const c of images) {
    const age = ageDays(c.v);
    let reason = null;
    if (c.tags.some(isKeepTag)) reason = 'release or long-lived pointer';
    else if (newest.has(c.v.id)) reason = `among the ${keepNewest} newest images`;
    else if (age <= agedDays) reason = `younger than ${agedDays}d`;

    if (!reason) {
      del.push({ version: c.v, reason: `${c.tags.join(',')}, ${age | 0}d` });
      continue;
    }
    const children = childrenOf(c.v.name);
    if (children === undefined) {
      throw new Error(
        `cannot read the manifest of kept image ${c.tags.join(',')} (${c.v.name}); refusing to prune`);
    }
    protectedDigests.add(c.v.name);
    for (const d of children) protectedDigests.add(d);
    keep.push({ version: c.v, reason });
  }

  for (const c of classified) {
    if (c.kind === 'image') continue;
    const age = ageDays(c.v);
    const belongsTo = c.kind === 'reference' ? c.refersTo : [c.v.name];
    if (belongsTo.some(d => protectedDigests.has(d))) {
      keep.push({ version: c.v, reason: 'belongs to a kept image' });
    } else if (age > untaggedDays) {
      const label = c.kind === 'reference' ? c.tags.join(',') : 'untagged';
      del.push({ version: c.v, reason: `${label}, not part of a kept image, ${age | 0}d` });
    } else {
      keep.push({ version: c.v, reason: `younger than ${untaggedDays}d` });
    }
  }

  return { keep, delete: del };
}

/** Digests an image index lists; [] for a single image manifest. */
function childDigests(manifest) {
  return Array.isArray(manifest?.manifests) ? manifest.manifests.map(m => m.digest) : [];
}

module.exports = { planRetention, childDigests, classify };
