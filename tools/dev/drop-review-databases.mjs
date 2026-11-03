#!/usr/bin/env node
/**
 * Removes the throwaway `modgud_review_*` databases that local integration runs leave
 * on the dedicated development container `postgres-dev` (see
 * docs/contribute/developing-locally.md). The test fixtures never drop them, so failed
 * runs stay inspectable; this is the explicit clean-up.
 *
 * Dry run by default: prints what WOULD be dropped. Nothing is dropped without --apply.
 *
 *   node tools/dev/drop-review-databases.mjs                 # dry run, all modgud_review_*
 *   node tools/dev/drop-review-databases.mjs --apply         # drop them
 *   node tools/dev/drop-review-databases.mjs --prefix=modgud_review_cs   # narrower selection
 *
 * Guards (any failing one stops before anything is touched):
 *   - the container is `postgres-dev`, running, published exclusively on 127.0.0.1:5433
 *     and backed by the `modgud-postgres-dev-data` volume — never the stable `postgres`;
 *   - only names matching ^modgud_review_[a-z0-9_-]+$ (never modgud, postgres, templates);
 *   - a database with an open connection (e.g. a test that is still running) is skipped,
 *     never force-disconnected.
 * Connects inside the container as `modgud_dev`; no connection string or password is
 * read or printed.
 */
import { execFileSync } from 'node:child_process';

const CONTAINER = 'postgres-dev';
const VOLUME = 'modgud-postgres-dev-data';
const NAME = /^modgud_review_[a-z0-9_-]+$/; // realm slugs may contain "-"

const args = process.argv.slice(2);
const apply = args.includes('--apply');
const prefixArg = args.find(a => a.startsWith('--prefix='));
const prefix = prefixArg ? prefixArg.slice('--prefix='.length) : 'modgud_review_';
const unknown = args.filter(a => a !== '--apply' && !a.startsWith('--prefix='));
if (unknown.length || !prefix.startsWith('modgud_review_') || !/^[a-z0-9_]+$/.test(prefix)) {
  console.error('Usage: node tools/dev/drop-review-databases.mjs [--apply] [--prefix=modgud_review_<...>]');
  process.exit(2);
}

const docker = (...a) => execFileSync('docker', a, { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }).trim();
const psql = sql => docker('exec', CONTAINER, 'psql', '-U', 'modgud_dev', '-d', 'postgres',
  '-v', 'ON_ERROR_STOP=1', '-At', '-F', '\t', '-c', sql);

function verifyContainer() {
  let state, ports, mounts;
  try {
    state = docker('inspect', '--format', '{{.State.Status}}', CONTAINER);
    ports = JSON.parse(docker('inspect', '--format', '{{json .HostConfig.PortBindings}}', CONTAINER));
    mounts = JSON.parse(docker('inspect', '--format', '{{json .Mounts}}', CONTAINER));
  } catch {
    throw new Error(`container ${CONTAINER} not found`);
  }
  if (state !== 'running') throw new Error(`${CONTAINER} is ${state}, not running`);
  const bindings = Object.entries(ports ?? {});
  const exclusive = bindings.length === 1 && bindings[0][0] === '5432/tcp' &&
    bindings[0][1]?.length === 1 && bindings[0][1][0].HostIp === '127.0.0.1' && bindings[0][1][0].HostPort === '5433';
  if (!exclusive) throw new Error(`${CONTAINER} is not published exclusively on 127.0.0.1:5433`);
  if (!mounts.some(m => m.Type === 'volume' && m.Name === VOLUME))
    throw new Error(`${CONTAINER} does not use the dedicated ${VOLUME} volume`);
}

const mb = bytes => `${(bytes / 1024 / 1024).toFixed(0)} MB`;

try {
  verifyContainer();
} catch (error) {
  console.error(`Refusing: ${error.message}. Nothing was touched.`);
  process.exit(1);
}

const rows = psql(
  "SELECT d.datname, pg_database_size(d.datname), " +
  "(SELECT count(*) FROM pg_stat_activity a WHERE a.datname = d.datname) " +
  "FROM pg_database d WHERE NOT d.datistemplate ORDER BY d.datname")
  .split('\n').filter(Boolean).map(line => {
    const [name, size, active] = line.split('\t');
    return { name, size: Number(size), active: Number(active) };
  })
  .filter(r => NAME.test(r.name) && r.name.startsWith(prefix));

const busy = rows.filter(r => r.active > 0);
const targets = rows.filter(r => r.active === 0);
const total = targets.reduce((sum, r) => sum + r.size, 0);
console.log(`${CONTAINER} (127.0.0.1:5433): ${rows.length} database(s) match ${prefix}*`);
console.log(`  ${targets.length} without open connections, ${mb(total)} in total`);
if (busy.length) console.log(`  ${busy.length} skipped (open connections): ${busy.map(r => r.name).join(', ')}`);

if (!apply) {
  console.log('Dry run: nothing dropped. Re-run with --apply to drop the databases above.');
  process.exit(0);
}

let dropped = 0;
const failed = [];
for (const { name } of targets) {
  if (!NAME.test(name)) continue; // re-checked right before the statement
  try {
    psql(`DROP DATABASE "${name}"`);
    dropped++;
  } catch {
    failed.push(name); // e.g. a connection opened in the meantime
  }
}
console.log(`Dropped ${dropped} database(s).${failed.length ? ` Not dropped: ${failed.join(', ')}` : ''}`);
process.exit(failed.length ? 1 : 0);
