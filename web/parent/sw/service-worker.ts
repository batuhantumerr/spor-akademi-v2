/**
 * The parent app's service worker (P3-07, DEC-394), built to `/sw.js` by pwa/plugin.ts, which replaces
 * `__SA_PRECACHE__` with this build's version and file list.
 *
 * - Install: precache the app shell (index.html, scripts, styles, icons, manifest) into a cache named after the
 *   version. A new version waits until the app asks it to take over ("Yeni sürüm hazır", DEC-394) or every tab
 *   of the old one is closed.
 * - Activate: delete older versions' caches, take control of open pages.
 * - Fetch: see policy.ts. API answers and anything else with personal data are never cached.
 */
import { cacheName, classify, isCacheable, OFFLINE_HTML, staleCaches } from './policy';

declare const self: ServiceWorkerGlobalScope;
declare const __SA_PRECACHE__: { readonly version: string; readonly files: readonly string[] };

const { version, files } = __SA_PRECACHE__;
const currentCache = cacheName(version);
const precache = new Set(files);

self.addEventListener('install', (event) => {
  event.waitUntil(
    caches
      .open(currentCache)
      // `reload`: straight from the server, never from the HTTP cache of an older version.
      .then((cache) => cache.addAll(files.map((file) => new Request(file, { cache: 'reload' })))),
  );
});

self.addEventListener('activate', (event) => {
  event.waitUntil(
    caches
      .keys()
      .then((keys) => Promise.all(staleCaches(keys, currentCache).map((key) => caches.delete(key))))
      .then(() => self.clients.claim()),
  );
});

self.addEventListener('message', (event) => {
  const data: unknown = event.data;
  if (typeof data === 'object' && data !== null && (data as { type?: unknown }).type === 'SKIP_WAITING') {
    void self.skipWaiting();
  }
});

self.addEventListener('fetch', (event) => {
  const kind = classify(event.request, self.location.origin, precache);
  switch (kind) {
    case 'navigation':
      event.respondWith(navigate(event.request));
      return;
    case 'precached':
    case 'asset':
      event.respondWith(cacheFirst(event.request, kind === 'asset'));
      return;
    case 'passthrough':
      // Not answered: the browser fetches it as if there were no worker, and nothing is stored.
      return;
  }
});

/** Network first, so a deployment is seen at once; offline, the cached shell, which shows "Bağlantı yok". */
async function navigate(request: Request): Promise<Response> {
  try {
    return await fetch(request);
  } catch {
    const shell = await caches.match('/index.html', { cacheName: currentCache });
    return (
      shell ??
      new Response(OFFLINE_HTML, {
        status: 503,
        headers: { 'Content-Type': 'text/html; charset=utf-8', 'Cache-Control': 'no-store' },
      })
    );
  }
}

/** Build output never changes under its name: the cached copy is the right one. */
async function cacheFirst(request: Request, storeOnMiss: boolean): Promise<Response> {
  const cache = await caches.open(currentCache);
  const cached = await cache.match(request);
  if (cached) return cached;
  const response = await fetch(request);
  if (storeOnMiss && isCacheable(response)) {
    await cache.put(request, response.clone());
  }
  return response;
}
