// Shared helpers of the k6 scenarios (P4-08). Synthetic demo accounts only: the addresses come from the seeder's
// manifest (scripts/perf/prepare-db.sh), the password from DEMO_PASSWORD in the environment; neither is logged.
import http from 'k6/http';
import { check, fail } from 'k6';

export const BASE_URL = __ENV.BASE_URL || 'http://api-1:8080';
const PASSWORD = __ENV.DEMO_PASSWORD;
const MANIFEST = JSON.parse(open(__ENV.MANIFEST || '../out/manifest.json'));

if (!PASSWORD) {
  fail('DEMO_PASSWORD is not set');
}

export const schools = MANIFEST.schools;

/** Every request carries a template name (one series per endpoint, not per id) and its kind: read, write or auth. */
export function params(token, name, kind, extra = {}) {
  const headers = { Accept: 'application/json', 'Accept-Language': 'tr', ...(extra.headers || {}) };
  if (token) headers.Authorization = `Bearer ${token}`;
  const result = { headers, tags: { name, kind } };
  // Only when given: an undefined callback is not the default one (2xx and 3xx are expected).
  if (extra.responseCallback) result.responseCallback = extra.responseCallback;
  return result;
}

/** Signs in and returns the access token and the refresh cookie (the API sets it Secure, so k6 would not resend it over http). */
export function signIn(email) {
  const response = http.post(
    `${BASE_URL}/api/v1/auth/login`,
    JSON.stringify({ email, password: PASSWORD }),
    params(null, 'POST /auth/login', 'auth', { headers: { 'Content-Type': 'application/json' } }),
  );
  const ok = check(response, { 'sign-in 200': (r) => r.status === 200 });
  if (!ok) return null;
  const cookie = (response.cookies.sa_refresh || [])[0];
  return { token: response.json('accessToken'), refreshCookie: cookie ? `sa_refresh=${cookie.value}` : null };
}

export function get(token, path, name, kind = 'read') {
  const response = http.get(`${BASE_URL}${path}`, params(token, name, kind));
  check(response, { [`${name} 2xx`]: (r) => r.status >= 200 && r.status < 300 });
  return response;
}

export function send(method, token, path, body, name, expected = [200, 201]) {
  const response = http.request(
    method,
    `${BASE_URL}${path}`,
    JSON.stringify(body),
    params(token, name, 'write', {
      headers: { 'Content-Type': 'application/json' },
      responseCallback: http.expectedStatuses(...expected),
    }),
  );
  const ok = check(response, { [`${name} ${expected.join('/')}`]: (r) => expected.includes(r.status) });
  if (!ok) {
    // The status and the error code only (problem details carry no personal data in "code").
    let code = '';
    try { code = `${response.json('code') || ''} ${JSON.stringify(response.json('errors') || {})}`; } catch { code = ''; }
    console.warn(`${name}: ${response.status} ${code}`);
  }
  return response;
}

export function pick(items) {
  return items[Math.floor(Math.random() * items.length)];
}

export function between(min, max) {
  return min + Math.random() * (max - min);
}

/** The schools are in Turkey (Europe/Istanbul, UTC+3 all year since 2016): dates as the school sees them. */
export function istanbulDate(daysAgo = 0) {
  const shifted = new Date(Date.now() + 3 * 3600 * 1000 - daysAgo * 86400 * 1000);
  return shifted.toISOString().slice(0, 10);
}

export function currentPeriod() {
  return istanbulDate(0).slice(0, 7);
}

/**
 * The endpoints named in the report: each gets a lax threshold so that k6 keeps its own p95 series (the budget itself
 * is checked per kind, see load.js).
 */
export const ENDPOINTS = {
  read: [
    'GET /dashboard',
    'GET /students',
    'GET /students search',
    'GET /students/:id',
    'GET /dues/outstanding',
    'GET /groups',
    'GET /payments',
    'GET /reports/attendance',
    'GET /reports/occupancy',
    'GET /reports/revenue',
    'GET /me/groups',
    'GET /me/sessions',
    'GET /groups/:id/roster',
    'GET /groups/:id/sessions/:key/attendance',
    'GET /me/announcements',
    'GET /parent/children',
    'GET /parent/children/:id/schedule',
    'GET /parent/children/:id/dues',
    'GET /parent/children/:id/payments',
    'GET /parent/children/:id/attendance',
    'GET /parent/children/:id/announcements',
  ],
  write: ['PUT /groups/:id/sessions/:key/attendance', 'POST /payments'],
  auth: ['POST /auth/login'],
};
