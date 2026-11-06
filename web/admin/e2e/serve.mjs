// Serves the production build (dist/admin/browser) for the e2e suite the way the deployment does:
// static files with an index.html fallback for client routes (docker/admin/nginx.conf), and /api and
// /health proxied to the API on the same origin (ADR-0006: no CORS). No file watching or live reload,
// so nothing can restart under a running test. Node built-ins only.
//
// HTTPS, like production (NFR-SEC-02), with the throwaway self-signed certificate scripts/e2e.sh
// creates per run: the refresh cookie is `Secure`, and WebKit does not send Secure cookies to
// http://localhost, so a plain-HTTP server would hide the session restore from the WebKit tests.
//
// Usage: E2E_TLS_KEY=<pem> E2E_TLS_CERT=<pem> node e2e/serve.mjs <port> <api-origin>
import { createReadStream, readFileSync, statSync } from 'node:fs';
import { request } from 'node:http';
import { createServer } from 'node:https';
import { extname, join, normalize, resolve, sep } from 'node:path';

const [port, apiOrigin] = [Number(process.argv[2]), new URL(process.argv[3])];
const tls = {
  key: readFileSync(process.env.E2E_TLS_KEY),
  cert: readFileSync(process.env.E2E_TLS_CERT),
};
const root = resolve(import.meta.dirname, '..', 'dist', 'admin', 'browser');
const types = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.json': 'application/json',
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
  const path = decodeURIComponent(new URL(req.url ?? '/', 'http://localhost').pathname);
  if (path === '/api' || path.startsWith('/api/') || path.startsWith('/health/')) {
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
    .on('error', (error) => {
      // A machine without IPv6 loopback still serves on 127.0.0.1.
      if (host !== '::1') throw error;
    })
    .listen(port, host);
}
console.log(`e2e: admin app on https://localhost:${port}, API proxied to ${apiOrigin.origin}`);
