/**
 * What the service worker may do with a request (P3-07, DEC-394). Pure functions, so the rules that keep personal
 * data out of the caches are unit tested (policy.test.ts) without a browser.
 *
 * The worker only ever caches the static app shell and hashed build output. API answers, health checks and
 * anything sent with credentials are never touched: the browser handles them as if there were no worker.
 */

/** Every cache this app creates starts with this; `activate` deletes the ones of older versions. */
export const CACHE_PREFIX = 'sa-parent-';

/**
 * - `navigation`: an app route (network first; offline, the cached `index.html`).
 * - `precached`: a file of this version's precache list (cache first).
 * - `asset`: other hashed build output under `/assets/` (cache first, cached on first use; e.g. font subsets).
 * - `passthrough`: everything else, never answered or cached by the worker.
 */
export type RequestKind = 'navigation' | 'precached' | 'asset' | 'passthrough';

/** The parts of a `Request` the decision needs (a real Request satisfies it). */
export interface RequestLike {
  readonly url: string;
  readonly method: string;
  readonly mode: string;
  readonly headers: { has(name: string): boolean };
}

/** Paths that belong to the server, never to the app: the API (including /api/v1/auth) and health checks. */
export function isServerPath(pathname: string): boolean {
  const path = pathname.toLowerCase();
  return path === '/api' || path.startsWith('/api/') || path === '/health' || path.startsWith('/health/');
}

/** A client route of the SPA: not a server path and its last segment has no file extension. */
export function isAppRoute(pathname: string): boolean {
  if (isServerPath(pathname)) return false;
  const last = pathname.slice(pathname.lastIndexOf('/') + 1);
  return !last.includes('.');
}

export function cacheName(version: string): string {
  return CACHE_PREFIX + version;
}

/**
 * Decides how the worker treats a request. `precache` holds this version's precached paths (`/index.html`,
 * `/assets/index-abc123.js`, ...).
 */
export function classify(request: RequestLike, origin: string, precache: ReadonlySet<string>): RequestKind {
  if (request.method !== 'GET') return 'passthrough';
  let url: URL;
  try {
    url = new URL(request.url);
  } catch {
    return 'passthrough';
  }
  if (url.origin !== origin) return 'passthrough';
  if (isServerPath(url.pathname)) return 'passthrough';
  // Defence in depth: the app's static files are never requested with credentials or a range.
  if (request.headers.has('authorization') || request.headers.has('range')) return 'passthrough';
  if (request.mode === 'navigate') return isAppRoute(url.pathname) ? 'navigation' : 'passthrough';
  // Query strings are not part of any build file name; a request with one is not a precached file.
  if (url.search !== '') return 'passthrough';
  if (precache.has(url.pathname)) return 'precached';
  if (url.pathname.startsWith('/assets/') && url.pathname.length > '/assets/'.length) return 'asset';
  return 'passthrough';
}

/** The parts of a `Response` the decision needs. */
export interface ResponseLike {
  readonly status: number;
  readonly type: string;
  readonly headers: { get(name: string): string | null };
}

/** Only a complete, same-origin, storable answer goes into a cache. */
export function isCacheable(response: ResponseLike): boolean {
  if (response.status !== 200 || response.type !== 'basic') return false;
  const cacheControl = (response.headers.get('cache-control') ?? '').toLowerCase();
  return !/(^|[\s,])(no-store|private)([\s,=]|$)/.test(cacheControl);
}

/** Caches of this app that do not belong to the current version (deleted on `activate`). */
export function staleCaches(keys: readonly string[], current: string): string[] {
  return keys.filter((key) => key.startsWith(CACHE_PREFIX) && key !== current);
}

/** Shown when a page is opened offline and the app shell is not cached (yet). No script, no external file. */
export const OFFLINE_HTML = `<!doctype html>
<html lang="tr">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<meta name="color-scheme" content="light dark">
<title>Bağlantı yok · Spor Akademi</title>
</head>
<body style="font-family: system-ui, sans-serif; margin: 0; padding: 2rem 1rem; line-height: 1.5">
<main style="max-width: 32rem; margin: 0 auto">
<h1 style="font-size: 1.25rem">Bağlantı yok</h1>
<p>İnternet bağlantınız yok gibi görünüyor. Bağlantınız gelince sayfayı yenileyin.</p>
</main>
</body>
</html>
`;
