/** Reads `token` from a URL fragment such as `#token=abc`. */
export function tokenFromFragment(fragment: string): string | null {
  const token = new URLSearchParams(fragment.replace(/^#/, '')).get('token');
  return token && token.length <= 128 ? token : null;
}

/** Pages for signed-out visitors: returning to them after a login would bounce between the guards. */
const GUEST_PATHS = ['/login', '/forgot-password', '/reset-password'];

/** Control characters and the backslash: browsers drop tabs/newlines and read `/\host` as another host. */
// eslint-disable-next-line no-control-regex -- control characters are exactly what is refused here.
const UNSAFE = /[\u0000-\u001f\u007f\\]/;

/**
 * A return target after login: only a path of this app (an open redirect otherwise). The value must start with a
 * single `/`, carry no control characters or backslashes, resolve to this origin and not be a guest page. Anything
 * else returns home.
 */
export function safeReturnUrl(value: string | null): string {
  if (!value?.startsWith('/') || value.startsWith('//') || UNSAFE.test(value)) return '/';
  let url: URL;
  try {
    url = new URL(value, window.location.origin);
  } catch {
    return '/';
  }
  if (url.origin !== window.location.origin) return '/';
  if (GUEST_PATHS.some((path) => url.pathname === path || url.pathname.startsWith(`${path}/`))) return '/';
  return url.pathname + url.search + url.hash;
}
