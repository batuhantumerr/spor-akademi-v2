/** Reads `token` from a URL fragment such as `#token=abc`. */
export function tokenFromFragment(fragment: string): string | null {
  const token = new URLSearchParams(fragment.replace(/^#/, '')).get('token');
  return token && token.length <= 128 ? token : null;
}

/** Only `/…` paths of this app may be return targets (no `//host` or scheme): an open redirect otherwise. */
export function safeReturnUrl(value: string | null): string {
  return value?.startsWith('/') && !value.startsWith('//') && !value.startsWith('/\\') ? value : '/';
}
