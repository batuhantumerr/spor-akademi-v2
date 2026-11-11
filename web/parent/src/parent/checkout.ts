/** Online payment helpers (DEC-386, DEC-347, DEC-348): where the browser goes and what it shows on return. */

/**
 * Where the browser may be sent to pay: this app's own pages (the Fake provider's `/sandbox-odeme`) or an https
 * provider page. Anything else (a `javascript:` URL, plain http elsewhere) is refused.
 */
export function checkoutTarget(checkoutUrl: string | null | undefined): string | null {
  if (!checkoutUrl) return null;
  let url: URL;
  try {
    url = new URL(checkoutUrl, window.location.origin);
  } catch {
    return null;
  }
  if (url.origin === window.location.origin || url.protocol === 'https:') return url.href;
  return null;
}

/** Navigates away to the checkout; a seam for tests. */
export const browser = {
  assign(url: string): void {
    window.location.assign(url);
  },
};

/**
 * Sends the browser to the provider callback like a hosted checkout does: a form POST with the token, so the API
 * answers with a redirect to the result page of this origin (`/odeme-sonucu`).
 */
export function postToCallback(callbackUrl: string, token: string): void {
  const form = document.createElement('form');
  form.method = 'post';
  form.action = callbackUrl;
  const input = document.createElement('input');
  input.type = 'hidden';
  input.name = 'token';
  input.value = token;
  form.appendChild(input);
  document.body.appendChild(form);
  form.submit();
}

const KNOWN = ['Succeeded', 'Failed', 'Pending', 'Expired'];

/** The status shown for the `durum` query parameter; anything unexpected reads as "unknown". */
export function resultStatus(value: string | null): string {
  if (value === 'Created') return 'Pending';
  return value && KNOWN.includes(value) ? value : 'Unknown';
}
