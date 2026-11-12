// Serves the production build (dist/admin/browser) for the e2e suite the way the deployment does:
// static files with an index.html fallback for client routes (docker/admin/nginx.conf), and /api, /health and
// /hubs (SignalR, with the WebSocket upgrade, P3-08) proxied to the API on the same origin (ADR-0006: no
// CORS). No file watching or live reload, so nothing can restart under a running test. Node built-ins only.
//
// HTTPS, like production (NFR-SEC-02), with the throwaway self-signed certificate scripts/e2e.sh
// creates per run: the refresh cookie is `Secure`, and WebKit does not send Secure cookies to
// http://localhost, so a plain-HTTP server would hide the session restore from the WebKit tests.
//
// Usage: E2E_TLS_KEY=<pem> E2E_TLS_CERT=<pem> node e2e/serve.mjs <port> <api-origin> [<build-dir>]
// <build-dir> defaults to the admin build (dist/admin/browser); the parent app passes ../parent/dist (P3-05).
import { createReadStream, readFileSync, statSync } from 'node:fs';
import { request } from 'node:http';
import { connect } from 'node:net';
import { createServer } from 'node:https';
import { extname, join, normalize, resolve, sep } from 'node:path';

const [port, apiOrigin] = [Number(process.argv[2]), new URL(process.argv[3])];
const buildDir = process.argv[4] ?? join('dist', 'admin', 'browser');
const tls = {
  key: readFileSync(process.env.E2E_TLS_KEY),
  cert: readFileSync(process.env.E2E_TLS_CERT),
};
const root = resolve(import.meta.dirname, '..', buildDir);
const types = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.json': 'application/json',
  '.webmanifest': 'application/manifest+json',
  '.svg': 'image/svg+xml',
  '.png': 'image/png',
  '.ico': 'image/x-icon',
  '.woff2': 'font/woff2',
  '.txt': 'text/plain; charset=utf-8',
};

function proxy(req, res) {
  const upstream = request(
    {
      host: apiOrigin.hostname,
      port: apiOrigin.port,
      method: req.method,
      path: req.url,
      headers: { ...req.headers, host: apiOrigin.host },
    },
    (answer) => {
      res.writeHead(answer.statusCode ?? 502, answer.headers);
      answer.pipe(res);
    },
  );
  upstream.on('error', () => {
    res.writeHead(502).end();
  });
  req.pipe(upstream);
}

const isApiPath = (path) =>
  path === '/api' ||
  path.startsWith('/api/') ||
  path.startsWith('/health/') ||
  path === '/hubs' ||
  path.startsWith('/hubs/');

// P3-08: the hub's WebSocket. The upgrade request is replayed to the API as it came and the two sockets are joined.
function upgrade(req, socket, head) {
  let path;
  try {
    path = new URL(req.url ?? '/', 'http://localhost').pathname;
  } catch {
    socket.destroy();
    return;
  }
  if (path !== '/hubs' && !path.startsWith('/hubs/')) {
    socket.destroy();
    return;
  }
  const upstream = connect(Number(apiOrigin.port), apiOrigin.hostname, () => {
    const headers = Object.entries({ ...req.headers, host: apiOrigin.host })
      .map(([name, value]) => `${name}: ${Array.isArray(value) ? value.join(', ') : value}`)
      .join('\r\n');
    upstream.write(`${req.method} ${req.url} HTTP/1.1\r\n${headers}\r\n\r\n`);
    if (head?.length) upstream.write(head);
    upstream.pipe(socket);
    socket.pipe(upstream);
  });
  upstream.on('error', () => socket.destroy());
  socket.on('error', () => upstream.destroy());
}

function file(path) {
  const full = normalize(join(root, path));
  if (!full.startsWith(root + sep)) {
    return null;
  }
  try {
    return statSync(full).isFile() ? full : null;
  } catch {
    return null;
  }
}

function handle(req, res) {
  let path;
  try {
    path = decodeURIComponent(new URL(req.url ?? '/', 'http://localhost').pathname);
  } catch {
    res.writeHead(400).end(); // malformed percent-encoding
    return;
  }
  if (isApiPath(path)) {
    proxy(req, res);
    return;
  }
  // Hashed assets are served as they are; every other path is a client route (index.html).
  const target = file(path) ?? join(root, 'index.html');
  res.writeHead(200, { 'Content-Type': types[extname(target)] ?? 'application/octet-stream' });
  createReadStream(target).pipe(res);
}

// "localhost" resolves to either loopback address depending on the browser; listen on both, never
// on an external interface.
for (const host of ['127.0.0.1', '::1']) {
  createServer(tls, handle)
    .on('upgrade', upgrade)
    .on('error', (error) => {
      // A machine without IPv6 loopback still serves on 127.0.0.1; any other failure is fatal.
      const noIpv6 = error.code === 'EADDRNOTAVAIL' || error.code === 'EAFNOSUPPORT';
      if (host !== '::1' || !noIpv6) throw error;
    })
    .listen(port, host);
}
console.log(`e2e: ${root} on https://localhost:${port}, API proxied to ${apiOrigin.origin}`);
