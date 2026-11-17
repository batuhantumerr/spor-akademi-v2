#!/usr/bin/env node
// DEC-670: `npm audit` gate with a strict, expiring allowlist (.github/npm-audit-allowlist.json).
// Usage: node scripts/npm-audit.mjs [app-dir]   (default: current directory; the app name is the directory name)
// High and critical advisories fail unless the GHSA id has an unexpired allowlist entry for this app.
// Expired, malformed and unused (stale) entries fail too, so the list cannot rot.
import { spawnSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { basename, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const BLOCKING = new Set(['high', 'critical']);
const GHSA = /GHSA(?:-[2-9cfghjmpqrvwx]{4}){3}/i;
const DATE = /^\d{4}-\d{2}-\d{2}$/;

/** Unique high/critical advisories from `npm audit --json` output (vulnerabilities -> via objects). */
export function collectAdvisories(report) {
  const found = new Map();
  for (const vulnerability of Object.values(report?.vulnerabilities ?? {})) {
    for (const via of vulnerability.via ?? []) {
      if (typeof via === 'string' || !BLOCKING.has(via.severity)) continue;
      const ghsa = GHSA.exec(via.url ?? '') ?? GHSA.exec(String(via.source ?? ''));
      const id = ghsa ? normalizeId(ghsa[0]) : `advisory-${via.source ?? via.url ?? via.title ?? 'unknown'}`;
      const entry = found.get(id) ?? { id, severity: via.severity, title: via.title ?? '', url: via.url ?? '', packages: new Set() };
      if (via.severity === 'critical') entry.severity = 'critical';
      entry.packages.add(via.name ?? vulnerability.name);
      found.set(id, entry);
    }
  }
  return [...found.values()].map((a) => ({ ...a, packages: [...a.packages].sort() }));
}

/** GitHub writes GHSA ids as GHSA-xxxx-xxxx-xxxx: prefix upper case, the rest lower case. */
function normalizeId(value) {
  return `GHSA${value.slice(4).toLowerCase()}`;
}

/** Why an `npm audit --json` result cannot be trusted, or null. A report without `vulnerabilities` must never count as clean. */
export function reportProblem(report) {
  if (report === null || typeof report !== 'object' || Array.isArray(report)) return 'npm audit returned an unexpected JSON value.';
  if (report.error) return `npm audit failed: ${report.error.summary ?? JSON.stringify(report.error)}`;
  if (report.vulnerabilities === null || typeof report.vulnerabilities !== 'object' || Array.isArray(report.vulnerabilities)) {
    return 'npm audit JSON has no "vulnerabilities" object; refusing to treat it as clean.';
  }
  return null;
}

function validDate(value) {
  if (typeof value !== 'string' || !DATE.test(value)) return false;
  const parsed = new Date(`${value}T00:00:00Z`);
  return !Number.isNaN(parsed.getTime()) && parsed.toISOString().startsWith(value);
}

/** Pure decision. `today` is YYYY-MM-DD in UTC. An entry is valid through its `expires` day, expired after it. */
export function evaluate({ advisories, allowlist, app, today }) {
  const errors = [];
  const allowed = [];
  const blocking = [];
  const entries = Array.isArray(allowlist?.entries) ? allowlist.entries : null;
  if (entries === null) errors.push('allowlist: "entries" must be an array');
  const usable = new Map();
  for (const [index, entry] of (entries ?? []).entries()) {
    const label = `allowlist entry #${index + 1} (${entry?.id ?? 'no id'})`;
    const problems = [];
    if (typeof entry?.id !== 'string' || !new RegExp(`^${GHSA.source}$`, 'i').test(entry.id)) problems.push('"id" must be a GHSA id');
    if (typeof entry?.reason !== 'string' || entry.reason.trim() === '') problems.push('"reason" is required');
    if (!validDate(entry?.expires)) problems.push('"expires" must be a valid YYYY-MM-DD date');
    if (!Array.isArray(entry?.apps) || entry.apps.length === 0 || entry.apps.some((a) => typeof a !== 'string' || a === '')) {
      problems.push('"apps" must be a non-empty list of app names');
    }
    if (problems.length > 0) {
      errors.push(`${label} is malformed: ${problems.join('; ')}`);
      continue;
    }
    const id = normalizeId(entry.id);
    if (usable.has(id)) {
      errors.push(`${label} is a duplicate`);
      continue;
    }
    usable.set(id, entry);
    if (!entry.apps.includes(app)) continue;
    if (entry.expires < today) errors.push(`${label} expired on ${entry.expires}: fix the advisory or have the owner renew it`);
  }
  const seen = new Set();
  for (const advisory of advisories) {
    seen.add(advisory.id);
    const entry = usable.get(advisory.id);
    if (entry && entry.apps.includes(app) && entry.expires >= today) allowed.push({ advisory, entry });
    else blocking.push(advisory);
  }
  for (const [id, entry] of usable) {
    if (entry.apps.includes(app) && !seen.has(id)) errors.push(`allowlist entry ${id} is unused (stale) for ${app}: remove it`);
  }
  return { ok: errors.length === 0 && blocking.length === 0, errors, allowed, blocking };
}

function main() {
  const dir = resolve(process.argv[2] ?? '.');
  const app = basename(dir);
  const allowlistPath = join(fileURLToPath(new URL('.', import.meta.url)), '..', '.github', 'npm-audit-allowlist.json');
  const today = new Date().toISOString().slice(0, 10);

  let allowlist;
  try {
    allowlist = JSON.parse(readFileSync(allowlistPath, 'utf8'));
  } catch (error) {
    console.error(`Cannot read ${allowlistPath}: ${error.message}`);
    process.exit(1);
  }

  const run = spawnSync('npm', ['audit', '--json'], { cwd: dir, encoding: 'utf8', shell: process.platform === 'win32', maxBuffer: 64 * 1024 * 1024 });
  if (run.error || typeof run.stdout !== 'string') {
    console.error(`Could not run npm audit: ${run.error?.message ?? 'no output'}`);
    process.exit(1);
  }
  let report;
  try {
    report = JSON.parse(run.stdout);
  } catch {
    console.error(`npm audit did not return JSON (exit ${run.status}).
${run.stderr ?? ''}`);
    process.exit(1);
  }
  const problem = reportProblem(report);
  if (problem) {
    console.error(problem);
    process.exit(1);
  }

  const result = evaluate({ advisories: collectAdvisories(report), allowlist, app, today });
  console.log(`npm audit (${app}, ${today} UTC): high and critical advisories fail unless allowlisted (DEC-670).`);
  for (const { advisory, entry } of result.allowed) {
    console.log(`ALLOWED  ${advisory.id} ${advisory.severity} [${advisory.packages.join(', ')}] until ${entry.expires}: ${entry.reason}`);
  }
  for (const advisory of result.blocking) {
    console.log(`BLOCKING ${advisory.id} ${advisory.severity} [${advisory.packages.join(', ')}] ${advisory.title} ${advisory.url}`);
  }
  for (const error of result.errors) console.log(`ERROR    ${error}`);
  console.log(result.ok ? 'Audit passed.' : 'Audit failed.');
  process.exit(result.ok ? 0 : 1);
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) main();
