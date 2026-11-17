// DEC-670: decision logic of scripts/npm-audit.mjs. Run by repo-checks: node --test scripts/npm-audit.test.mjs
import assert from 'node:assert/strict';
import { test } from 'node:test';
import { collectAdvisories, evaluate } from './npm-audit.mjs';

const ID = 'GHSA-fc5q-39xc-vjpf';
const advisory = { id: ID, severity: 'high', title: 't', url: `https://github.com/advisories/${ID}`, packages: ['picomatch'] };
const entry = { id: ID, reason: 'dev only', expires: '2026-12-17', apps: ['admin', 'parent'] };
const base = { advisories: [advisory], allowlist: { entries: [entry] }, app: 'admin', today: '2026-11-17' };

test('allowed: listed, unexpired, app matches', () => {
  const result = evaluate(base);
  assert.equal(result.ok, true);
  assert.equal(result.allowed.length, 1);
});

test('allowed on the expiry day, blocked the day after', () => {
  assert.equal(evaluate({ ...base, today: '2026-12-17' }).ok, true);
  const late = evaluate({ ...base, today: '2026-12-18' });
  assert.equal(late.ok, false);
  assert.equal(late.blocking.length, 1);
  assert.match(late.errors.join('\n'), /expired/);
});

test('stale: entry without a matching advisory fails', () => {
  const result = evaluate({ ...base, advisories: [] });
  assert.equal(result.ok, false);
  assert.match(result.errors.join('\n'), /unused \(stale\)/);
});

test('unknown high advisory blocks even when another entry is allowed', () => {
  const other = { ...advisory, id: 'GHSA-aaaa-bbbb-cccc' };
  const result = evaluate({ ...base, advisories: [advisory, other] });
  assert.equal(result.ok, false);
  assert.deepEqual(result.blocking.map((a) => a.id), ['GHSA-aaaa-bbbb-cccc']);
});

test('entry for another app does not allow this app', () => {
  const result = evaluate({ ...base, allowlist: { entries: [{ ...entry, apps: ['parent'] }] } });
  assert.equal(result.ok, false);
  assert.equal(result.blocking.length, 1);
});

test('malformed entries fail', () => {
  const bad = [{ ...entry, expires: '17-12-2026' }, { ...entry, expires: '2026-02-30' }, { ...entry, reason: '' }, { ...entry, id: 'CVE-1' }, { ...entry, apps: [] }];
  for (const item of bad) {
    const result = evaluate({ ...base, allowlist: { entries: [item] } });
    assert.equal(result.ok, false);
    assert.match(result.errors.join('\n'), /malformed/);
  }
  assert.equal(evaluate({ ...base, allowlist: {} }).ok, false);
});

test('collectAdvisories reads via objects, ignores strings and moderate severity, merges duplicates', () => {
  const report = {
    vulnerabilities: {
      picomatch: { name: 'picomatch', via: [{ source: 1, name: 'picomatch', severity: 'high', url: `https://github.com/advisories/${ID}`, title: 't' }] },
      vite: { name: 'vite', via: ['picomatch', { source: 1, name: 'picomatch', severity: 'high', url: `https://github.com/advisories/${ID}` }] },
      other: { name: 'other', via: [{ source: 2, name: 'other', severity: 'moderate', url: 'https://github.com/advisories/GHSA-aaaa-bbbb-cccc' }] },
    },
  };
  const found = collectAdvisories(report);
  assert.equal(found.length, 1);
  assert.equal(found[0].id, ID);
});
