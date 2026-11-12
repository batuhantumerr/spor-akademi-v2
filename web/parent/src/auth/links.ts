/** Reads `token` from a URL fragment such as `#token=abc`. */
export function tokenFromFragment(fragment: string): string | null {
  const token = new URLSearchParams(fragment.replace(/^#/, '')).get('token');
  return token && token.length <= 128 ? token : null;
}

/** The token of the link this visit opened; memory only, never stored. */
let linkToken: string | null = null;

/**
 * The single-use token of an invitation or reset link: read from the fragment when the page opens it, then kept in
 * memory, because the page removes the fragment at once. A language switch re-mounts the routed pages (DEC-426), and
 * the re-mounted page must still have the token (P3-11, DEC-439); a parent who switches to English must not lose
 * the link.
 */
export function takeLinkToken(fragment: string): string | null {
  const fresh = tokenFromFragment(fragment);
  if (fresh) linkToken = fresh;
  return linkToken;
}

/** Drops the remembered token once it is used or refused. */
export function forgetLinkToken(): void {
  linkToken = null;
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
