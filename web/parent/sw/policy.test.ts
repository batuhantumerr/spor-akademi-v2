import { describe, expect, it } from 'vitest';
import {
  CACHE_PREFIX,
  cacheName,
  classify,
  isAppRoute,
  isCacheable,
  OFFLINE_HTML,
  type RequestLike,
  staleCaches,
} from './policy';

const origin = 'https://veli.example.test';
const precache = new Set(['/index.html', '/assets/index-Ab12Cd34.js', '/manifest.webmanifest']);

function request(
  path: string,
  init: { method?: string; mode?: string; headers?: string[] } = {},
): RequestLike {
  const headers = new Set((init.headers ?? []).map((h) => h.toLowerCase()));
  return {
    url: path.startsWith('http') ? path : origin + path,
    method: init.method ?? 'GET',
    mode: init.mode ?? 'cors',
    headers: { has: (name: string) => headers.has(name.toLowerCase()) },
  };
}

function response(status: number, type: string, cacheControl: string | null = null, redirected = false) {
  return {
    status,
    type,
    redirected,
    headers: { get: (name: string) => (name === 'cache-control' ? cacheControl : null) },
  };
}

describe('service worker request policy (P3-07)', () => {
  it.each([
    '/api/v1/parent/children',
    '/api/v1/auth/refresh',
    '/api/v1/auth/login',
    '/api/v1/parent/online-payments',
    '/API/v1/parent/children',
    '/api',
    '/health/ready',
    '/health',
  ])('never handles the server path %s, not even as a navigation', (path) => {
    expect(classify(request(path), origin, precache)).toBe('passthrough');
    expect(classify(request(path, { mode: 'navigate' }), origin, precache)).toBe('passthrough');
  });

  it('leaves every non-GET request alone (login, payments, the sandbox form post)', () => {
    for (const method of ['POST', 'PUT', 'DELETE', 'PATCH', 'HEAD']) {
      expect(classify(request('/assets/index-Ab12Cd34.js', { method }), origin, precache)).toBe(
        'passthrough',
      );
      expect(classify(request('/odeme-sonucu', { method, mode: 'navigate' }), origin, precache)).toBe(
        'passthrough',
      );
    }
  });

  it('leaves other origins alone (payment provider, fonts from elsewhere)', () => {
    expect(
      classify(request('https://odeme.example.test/checkout', { mode: 'navigate' }), origin, precache),
    ).toBe('passthrough');
    expect(classify(request('https://cdn.example.test/assets/x.js'), origin, precache)).toBe('passthrough');
  });

  it('leaves requests with credentials or a range alone', () => {
    expect(
      classify(request('/assets/index-Ab12Cd34.js', { headers: ['Authorization'] }), origin, precache),
    ).toBe('passthrough');
    expect(classify(request('/assets/video.mp4', { headers: ['Range'] }), origin, precache)).toBe(
      'passthrough',
    );
  });

  it.each(['/', '/cocuk/0190a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b', '/login', '/reset-password', '/odeme-sonucu'])(
    'treats a navigation to the app route %s as a navigation',
    (path) => {
      expect(classify(request(path, { mode: 'navigate' }), origin, precache)).toBe('navigation');
      expect(classify(request(`${path}?durum=Succeeded`, { mode: 'navigate' }), origin, precache)).toBe(
        'navigation',
      );
    },
  );

  it('does not answer a navigation to a file with the app shell', () => {
    expect(classify(request('/sw.js', { mode: 'navigate' }), origin, precache)).toBe('passthrough');
    expect(classify(request('/manifest.webmanifest', { mode: 'navigate' }), origin, precache)).toBe(
      'passthrough',
    );
  });

  it('serves precached files and other hashed assets from the cache', () => {
    expect(classify(request('/assets/index-Ab12Cd34.js'), origin, precache)).toBe('precached');
    expect(classify(request('/manifest.webmanifest'), origin, precache)).toBe('precached');
    expect(classify(request('/index.html'), origin, precache)).toBe('precached');
    expect(classify(request('/assets/inter-cyrillic-wght-normal-Zz99.woff2'), origin, precache)).toBe(
      'asset',
    );
  });

  it.each([
    '/assets/index',
    '/assets/index/',
    '/assets/sub/',
    '/assets/sub/index-Ab12.js',
    '/assets/notes.txt',
    '/assets/page.html',
    '/assets/.js',
  ])('never caches %s under /assets/, which the SPA fallback could answer with index.html', (path) => {
    expect(classify(request(path), origin, precache)).toBe('passthrough');
  });

  it.each([
    '/assets/index-Ab12Cd34.css',
    '/assets/inter-latin-wght-normal-Dx4kXJAl.woff2',
    '/assets/old.woff',
    '/assets/logo-Ab12.svg',
    '/assets/icon-192-thONEyK8.png',
    '/assets/fav-Ab12.ico',
    '/assets/app-Ab12.webmanifest',
    '/assets/chunk-Ab12.JS',
  ])('caches the build file type %s on first use', (path) => {
    expect(classify(request(path), origin, precache)).toBe('asset');
  });

  it('does not handle files outside the build output or with a query string', () => {
    expect(classify(request('/sw.js'), origin, precache)).toBe('passthrough');
    expect(classify(request('/robots.txt'), origin, precache)).toBe('passthrough');
    expect(classify(request('/assets/'), origin, precache)).toBe('passthrough');
    expect(classify(request('/assets/index-Ab12Cd34.js?v=2'), origin, precache)).toBe('passthrough');
  });

  it('knows app routes from files and server paths', () => {
    expect(isAppRoute('/')).toBe(true);
    expect(isAppRoute('/cocuk/abc')).toBe(true);
    expect(isAppRoute('/favicon.ico')).toBe(false);
    expect(isAppRoute('/api/v1/parent/children')).toBe(false);
    expect(isAppRoute('/healthz')).toBe(true);
  });

  it('stores only complete same-origin answers the server allows to be stored', () => {
    expect(isCacheable(response(200, 'basic'))).toBe(true);
    // A redirect target (e.g. the SPA's index.html after a redirect) is not the asset that was asked for.
    expect(isCacheable(response(200, 'basic', null, true))).toBe(false);
    expect(isCacheable(response(200, 'basic', 'public, max-age=31536000, immutable'))).toBe(true);
    expect(isCacheable(response(200, 'basic', 'no-store'))).toBe(false);
    expect(isCacheable(response(200, 'basic', 'max-age=0, private'))).toBe(false);
    expect(isCacheable(response(206, 'basic'))).toBe(false);
    expect(isCacheable(response(404, 'basic'))).toBe(false);
    expect(isCacheable(response(200, 'opaque'))).toBe(false);
    expect(isCacheable(response(200, 'cors'))).toBe(false);
  });

  it('deletes only older caches of this app on activate', () => {
    const current = cacheName('v2');
    expect(staleCaches([cacheName('v1'), current, 'other-app', `${CACHE_PREFIX}v0`], current)).toEqual([
      cacheName('v1'),
      `${CACHE_PREFIX}v0`,
    ]);
  });

  it('has a self-contained Turkish offline page', () => {
    expect(OFFLINE_HTML).toContain('<html lang="tr">');
    expect(OFFLINE_HTML).toContain('Bağlantı yok');
    expect(OFFLINE_HTML).not.toMatch(/<script|src=|href=/);
  });
});
