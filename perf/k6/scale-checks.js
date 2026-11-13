// P4-08, NFR-SCL-01: behaviour behind the reverse proxy with two API instances (compose profile "scale", DEC-564).
// One virtual user, one pass, against the proxy with the production rate limits (scripts/perf/run-k6.sh checks):
//   1. session: a refresh cookie issued by one instance is accepted by the other (no session state in memory)
//   2. SignalR: negotiate and WebSocket connect may land on different instances; works only with hub affinity
//   3. backplane: an event raised on one instance reaches a connection held by the other (Valkey)
//   4. rate limits: each instance counts on its own (DEC-506), and the client is the forwarded address (DEC-507)
import http from 'k6/http';
import ws from 'k6/ws';
import { check } from 'k6';
import { BASE_URL, schools, signIn } from './lib/common.js';

const DIRECT_URL = __ENV.DIRECT_URL || 'http://api-1:8080';
const LABEL = __ENV.LABEL || 'scale-checks';

export const options = { vus: 1, iterations: 1, thresholds: { checks: ['rate>=0'] } };

// handleSummary runs apart from the virtual user, so the observation travels in the check's name: "check | observed".
function record(name, ok, detail) {
  check(null, { [`${name} | ${detail}`]: () => ok });
}

function servedBy(response) {
  return response.headers['X-Served-By'] || '?';
}

function refresh(cookie) {
  return http.post(`${BASE_URL}/api/v1/auth/refresh`, null, {
    headers: { 'X-Requested-With': 'XMLHttpRequest', ...(cookie ? { Cookie: cookie } : {}) },
    tags: { name: 'POST /auth/refresh', kind: 'auth' },
    responseCallback: http.expectedStatuses(200, 401, 429),
  });
}

function sessionAcrossInstances() {
  const admin = schools[0].admin;
  const login = http.post(`${BASE_URL}/api/v1/auth/login`, JSON.stringify({ email: admin, password: __ENV.DEMO_PASSWORD }), {
    headers: { 'Content-Type': 'application/json' },
  });
  let cookie = login.cookies.sa_refresh ? `sa_refresh=${login.cookies.sa_refresh[0].value}` : null;
  const seen = [servedBy(login)];
  let ok = login.status === 200 && cookie !== null;
  for (let i = 0; i < 3 && ok; i++) {
    const r = refresh(cookie);
    seen.push(servedBy(r));
    ok = r.status === 200 && Boolean(r.cookies.sa_refresh);
    if (ok) cookie = `sa_refresh=${r.cookies.sa_refresh[0].value}`;
  }
  const both = new Set(seen).size > 1;
  record('session: refresh works on every instance', ok && both, `login and 3 rotations served by ${seen.join(', ')}`);
  return signIn(admin);
}

function connectHub(token, onOpen, waitMs) {
  const negotiate = http.post(`${BASE_URL}/hubs/notifications/negotiate?negotiateVersion=1`, null, {
    headers: { Authorization: `Bearer ${token}` },
  });
  if (negotiate.status !== 200) return { negotiated: false, handshake: false, events: [], by: servedBy(negotiate) };
  const id = negotiate.json('connectionToken');
  const url = `${BASE_URL.replace(/^http/, 'ws')}/hubs/notifications?id=${id}&access_token=${token}`;
  const state = { negotiated: true, handshake: false, events: [], by: servedBy(negotiate) };
  ws.connect(url, {}, (socket) => {
    socket.on('open', () => socket.send('{"protocol":"json","version":1}\u001e'));
    socket.on('message', (data) => {
      for (const frame of String(data).split('\u001e').filter(Boolean)) {
        const message = JSON.parse(frame);
        if (!state.handshake && !message.type && !message.error) {
          state.handshake = true;
          if (onOpen) onOpen();
        } else if (message.type === 1 && message.target === 'Notify') {
          state.events.push(message.arguments[0].type);
        }
      }
    });
    socket.setTimeout(() => socket.close(), waitMs);
  });
  return state;
}

function hubAffinity(token) {
  let connected = 0;
  const attempts = 10;
  for (let i = 0; i < attempts; i++) {
    if (connectHub(token, null, 300).handshake) connected++;
  }
  record('signalr: negotiate + connect succeed through the proxy', connected === attempts, `${connected}/${attempts} connections completed the handshake`);
}

function backplane(token) {
  // Publishing goes through the proxy (round robin) while the connection is held; the outbox then delivers the event
  // from whichever instance processes it. Tried a few times, so that the publishing request lands on the other instance.
  const group = http.get(`${BASE_URL}/api/v1/groups?page=1&pageSize=1`, { headers: { Authorization: `Bearer ${token}` } }).json('items.0.id');
  const publishedOn = [];
  const state = connectHub(token, () => {
    for (let i = 0; i < 2; i++) {
      const r = http.post(`${BASE_URL}/api/v1/groups/${group}/announcements`, JSON.stringify({
        title: 'Yük testi duyurusu', body: 'Sentetik yük testi duyurusu (P4-08).',
      }), { headers: { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json' } });
      publishedOn.push(`${r.status}@${servedBy(r)}`);
    }
  }, 15000);
  const received = state.events.filter((e) => e === 'announcement.published').length;
  record('backplane: events from both instances reach one connection', received === 2,
    `connection on ${state.by}, published ${publishedOn.join(', ')}, received ${received}`);
}

function anonymousPost(base, path) {
  return http.post(`${base}${path}`, '{}', {
    headers: { 'Content-Type': 'application/json', 'X-Requested-With': 'XMLHttpRequest' },
    responseCallback: http.expectedStatuses(400, 401, 429),
  });
}

function rateLimitsPerInstance() {
  // The sign-in limits count per client address, endpoint and instance (10 a minute in production). Endpoints this run
  // has not used yet, so each starts with a fresh window; the requests are refused by validation or authentication,
  // after the limiter has counted them.
  const viaProxy = [];
  for (let i = 0; i < 6; i++) viaProxy.push(anonymousPost(BASE_URL, '/api/v1/auth/password-reset/confirm'));
  const direct = [];
  for (let i = 0; i < 10; i++) direct.push(anonymousPost(DIRECT_URL, '/api/v1/auth/password-reset/confirm'));
  const proxyByInstance = viaProxy.map(servedBy);
  const directAllowed = direct.filter((r) => r.status !== 429).length;
  const onApi1 = proxyByInstance.filter((b) => b.startsWith('api-1')).length;
  // The proxied requests that api-1 served count towards the same client as the direct ones only if api-1 took the
  // client from X-Forwarded-For; otherwise they would be the proxy's and the direct ones would get all 10.
  record('rate limit: the client is the forwarded address', onApi1 > 0 && directAllowed === 10 - onApi1,
    `proxied: ${proxyByInstance.join(', ')}; direct to api-1 allowed ${directAllowed} of 10 (expected ${10 - onApi1})`);

  const burst = [];
  for (let i = 0; i < 30; i++) burst.push(anonymousPost(BASE_URL, '/api/v1/auth/change-password'));
  const allowed = burst.filter((r) => r.status !== 429).length;
  record('rate limit: each instance counts on its own', allowed === 20,
    `${allowed} of 30 attempts through the proxy were not 429 (10 per instance and minute)`);
}

export default function () {
  const s = sessionAcrossInstances();
  if (!s) return;
  hubAffinity(s.token);
  backplane(s.token);
  rateLimitsPerInstance();
}

export function handleSummary(data) {
  const lines = [`## k6: ${LABEL} (${BASE_URL})`, '', '| check | result | observed |', '| --- | --- | --- |'];
  for (const c of data.root_group.checks) {
    const [name, detail] = c.name.split(' | ');
    lines.push(`| ${name} | ${c.fails === 0 ? 'ok' : 'FAILED'} | ${detail || ''} |`);
  }
  const text = `${lines.join('\n')}\n`;
  return { stdout: text, [`out/summary-${LABEL}.md`]: text };
}
