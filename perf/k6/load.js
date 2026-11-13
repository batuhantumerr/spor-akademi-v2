// P4-08: the load test (NFR-PRF-01 read p95 < 300 ms, NFR-PRF-02 write p95 < 500 ms). A realistic mix of the three
// school roles over the seeded demo and load test schools: School Admins (dashboard, students, dues, reports, payments),
// coaches (their sessions, attendance taken and corrected) and parents (children, dues, payments, attendance).
// Every virtual user signs in once with its own account and then works with think time like a person would.
//
//   PROFILE=load  (default) 300 virtual users at peak, the capacity assumption's concurrent users (appendix C §1)
//   PROFILE=smoke 12 virtual users for a minute: the scenario works and the budget holds at low load (CI)
//
// Run through scripts/perf/run-k6.sh, which prepares the API and passes BASE_URL, DEMO_PASSWORD and LABEL.
import { sleep } from 'k6';
import {
  BASE_URL, ENDPOINTS, between, currentPeriod, get, istanbulDate, pick, schools, send, signIn,
} from './lib/common.js';

const PROFILE = __ENV.PROFILE || 'load';
const LABEL = __ENV.LABEL || PROFILE;

// Peak virtual users per role: a tenth admins, two fifths coaches (attendance at training time), half parents.
const PEAK = PROFILE === 'smoke' ? { admin: 2, coach: 4, parent: 6 } : { admin: 30, coach: 120, parent: 150 };

function stages(peak) {
  return PROFILE === 'smoke'
    ? [{ duration: '10s', target: peak }, { duration: '50s', target: peak }]
    : [{ duration: '1m', target: peak }, { duration: '4m', target: peak }, { duration: '20s', target: 0 }];
}

function scenario(exec, peak) {
  return { executor: 'ramping-vus', exec, startVUs: 0, stages: stages(peak), gracefulRampDown: '10s', gracefulStop: '20s' };
}

const thresholds = {
  // The budget (NFR-PRF-01/02), over every read and every write of the run.
  'http_req_duration{kind:read}': ['p(95)<300'],
  'http_req_duration{kind:write}': ['p(95)<500'],
  http_req_failed: ['rate<0.01'],
  'http_req_failed{kind:read}': ['rate<0.01'],
  'http_req_failed{kind:write}': ['rate<0.01'],
  'http_req_failed{kind:auth}': ['rate<0.01'],
  checks: ['rate>0.99'],
};
// One series per endpoint for the report; the threshold is only there so that k6 keeps the series.
for (const name of [...ENDPOINTS.read, ...ENDPOINTS.write, ...ENDPOINTS.auth]) {
  thresholds[`http_req_duration{name:${name}}`] = ['max>=0'];
}

export const options = {
  scenarios: {
    admins: scenario('admin', PEAK.admin),
    coaches: scenario('coach', PEAK.coach),
    parents: scenario('parent', PEAK.parent),
  },
  thresholds,
  summaryTrendStats: ['avg', 'med', 'p(95)', 'p(99)', 'max', 'count'],
  // Each virtual user is one person on one connection pool (as a browser would be).
  noConnectionReuse: false,
  discardResponseBodies: false,
};

const admins = schools.map((s) => s.admin);
const coaches = schools.flatMap((s) => s.coaches);
const parents = schools.flatMap((s) => s.parents);
const SEARCHES = ['ay', 'el', 'me', 'ka', 'ye', 'ar', 'de', 'ze', 'yıl', 'öz'];
const STATUSES = ['Present', 'Present', 'Present', 'Present', 'Present', 'Present', 'Late', 'Absent', 'Excused'];

// Per virtual user: the session, and what it found so far (ids to open next).
let session = null;

function ensureSession(accounts) {
  if (session) return session;
  // Spread the virtual users over the accounts; several may share one (several tabs or devices of a person).
  session = signIn(accounts[(__VU - 1) % accounts.length]);
  return session;
}

function think() {
  sleep(between(1, 3));
}

export function admin() {
  const s = ensureSession(admins);
  if (!s) return sleep(5);
  const t = s.token;

  get(t, '/api/v1/dashboard', 'GET /dashboard');
  think();

  const list = get(t, `/api/v1/students?page=${1 + Math.floor(Math.random() * 5)}&pageSize=20`, 'GET /students');
  think();
  if (Math.random() < 0.6) {
    get(t, `/api/v1/students?search=${encodeURIComponent(pick(SEARCHES))}&pageSize=20`, 'GET /students search');
    think();
  }
  const students = list.status === 200 ? list.json('items') || [] : [];
  if (students.length > 0) {
    get(t, `/api/v1/students/${pick(students).id}`, 'GET /students/:id');
    think();
  }

  const period = currentPeriod();
  const dues = get(t, `/api/v1/dues/outstanding?period=${period}&page=1&pageSize=50`, 'GET /dues/outstanding');
  think();
  // A payment at the desk: a small part of a due of this month, so the due stays open for the next one.
  const open = dues.status === 200 ? (dues.json('items') || []).filter((d) => Number(d.remaining.amount) >= 1) : [];
  if (open.length > 0 && Math.random() < 0.5) {
    const due = pick(open);
    send('POST', t, '/api/v1/payments', {
      enrollmentId: due.enrollmentId,
      period: due.period,
      amount: { amount: '1.00', currency: due.remaining.currency },
      method: 'Cash',
      paidOn: istanbulDate(0),
    }, 'POST /payments', [201]);
    think();
  }

  get(t, '/api/v1/groups?page=1&pageSize=50', 'GET /groups');
  think();
  get(t, '/api/v1/payments?page=1&pageSize=20', 'GET /payments');
  think();

  if (Math.random() < 0.3) {
    const report = pick(['attendance', 'occupancy', 'revenue']);
    get(t, `/api/v1/reports/${report}`, `GET /reports/${report}`);
    think();
  }
}

export function coach() {
  const s = ensureSession(coaches);
  if (!s) return sleep(5);
  const t = s.token;

  const groups = get(t, '/api/v1/me/groups', 'GET /me/groups');
  think();
  get(t, `/api/v1/me/sessions?date=${istanbulDate(0)}`, 'GET /me/sessions');
  think();

  // Attendance of a session within the coach's 48-hour window: yesterday's, else the day before.
  for (const daysAgo of [1, 2]) {
    const day = get(t, `/api/v1/me/sessions?date=${istanbulDate(daysAgo)}`, 'GET /me/sessions');
    const sessions = day.status === 200 ? day.json() : [];
    if (sessions.length === 0) continue;
    const chosen = pick(sessions);
    const key = `${chosen.date}T${chosen.start}`;
    const sheet = get(t, `/api/v1/groups/${chosen.groupId}/sessions/${key}/attendance`, 'GET /groups/:id/sessions/:key/attendance');
    think();
    const students = sheet.status === 200 ? sheet.json('students') : [];
    if (students.length > 0) {
      send('PUT', t, `/api/v1/groups/${chosen.groupId}/sessions/${key}/attendance`, {
        records: students.map((st) => ({ studentId: st.studentId, status: pick(STATUSES) })),
      }, 'PUT /groups/:id/sessions/:key/attendance', [200]);
      think();
    }
    break;
  }

  const mine = groups.status === 200 ? groups.json() : [];
  if (mine.length > 0 && Math.random() < 0.5) {
    get(t, `/api/v1/groups/${pick(mine).id}/roster`, 'GET /groups/:id/roster');
    think();
  }
  get(t, '/api/v1/me/announcements?page=1&pageSize=20', 'GET /me/announcements');
  think();
}

export function parent() {
  const s = ensureSession(parents);
  if (!s) return sleep(5);
  const t = s.token;

  const children = get(t, '/api/v1/parent/children', 'GET /parent/children');
  think();
  const kids = children.status === 200 ? children.json() : [];
  if (kids.length === 0) return;
  const child = pick(kids).id;

  get(t, `/api/v1/parent/children/${child}/schedule`, 'GET /parent/children/:id/schedule');
  think();
  get(t, `/api/v1/parent/children/${child}/dues`, 'GET /parent/children/:id/dues');
  think();
  if (Math.random() < 0.5) {
    get(t, `/api/v1/parent/children/${child}/payments?page=1&pageSize=20`, 'GET /parent/children/:id/payments');
    think();
  }
  get(t, `/api/v1/parent/children/${child}/attendance`, 'GET /parent/children/:id/attendance');
  think();
  if (Math.random() < 0.5) {
    get(t, `/api/v1/parent/children/${child}/announcements?page=1&pageSize=20`, 'GET /parent/children/:id/announcements');
    think();
  }
}

function metric(data, key) {
  const m = data.metrics[key];
  return m ? m.values : null;
}

function fmt(v) {
  return v === undefined || v === null ? '-' : v.toFixed(1);
}

/** A Markdown table for docs/performans.md and the full k6 JSON next to it. */
export function handleSummary(data) {
  const lines = [];
  lines.push(`## k6: ${LABEL} (${PROFILE}, ${BASE_URL})`);
  lines.push('');
  const reqs = metric(data, 'http_reqs');
  const failed = metric(data, 'http_req_failed');
  const iterations = metric(data, 'iterations');
  const vus = metric(data, 'vus_max');
  lines.push(`requests: ${reqs ? reqs.count : '-'} (${reqs ? reqs.rate.toFixed(1) : '-'} RPS), failed: ${failed ? (failed.rate * 100).toFixed(2) : '-'} %, iterations: ${iterations ? iterations.count : '-'}, max VUs: ${vus ? vus.max : '-'}`);
  lines.push('');
  lines.push('| series | count | p50 ms | p95 ms | p99 ms | max ms |');
  lines.push('| --- | ---: | ---: | ---: | ---: | ---: |');
  const rows = [
    ['all reads', 'http_req_duration{kind:read}'],
    ['all writes', 'http_req_duration{kind:write}'],
    ...[...ENDPOINTS.read, ...ENDPOINTS.write, ...ENDPOINTS.auth].map((n) => [n, `http_req_duration{name:${n}}`]),
  ];
  for (const [label, key] of rows) {
    const v = metric(data, key);
    if (!v || !v.count) continue;
    lines.push(`| ${label} | ${v.count} | ${fmt(v.med)} | ${fmt(v['p(95)'])} | ${fmt(v['p(99)'])} | ${fmt(v.max)} |`);
  }
  lines.push('');
  const verdict = Object.entries(data.metrics)
    .filter(([, m]) => m.thresholds)
    .flatMap(([name, m]) => Object.entries(m.thresholds).filter(([expr]) => expr !== 'max>=0').map(([expr, t]) => `${name} ${expr}: ${t.ok ? 'ok' : 'FAILED'}`));
  lines.push(...verdict.map((v) => `- ${v}`));
  const text = `${lines.join('\n')}\n`;
  return {
    stdout: text,
    [`out/summary-${LABEL}.md`]: text,
    [`out/summary-${LABEL}.json`]: JSON.stringify(data, null, 2),
  };
}
