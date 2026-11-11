import createClient from 'openapi-fetch';
import { session } from '../auth/session';
import type { paths } from './schema.gen';

/**
 * A 401 means "refresh and retry" only when the token itself was rejected. Coded business 401s (for example
 * Auth.InvalidCredentials) go straight to the caller.
 */
async function isExpiredToken(response: Response): Promise<boolean> {
  let code: unknown;
  try {
    code = ((await response.clone().json()) as { code?: unknown }).code;
  } catch {
    code = undefined;
  }
  return code === undefined || code === 'Auth.Unauthenticated' || code === 'Auth.SessionExpired';
}

function withToken(request: Request, token: string | null): Request {
  if (!token) return request;
  const headers = new Headers(request.headers);
  headers.set('Authorization', `Bearer ${token}`);
  return new Request(request, { headers });
}

/**
 * Adds the in-memory access token to same-origin API calls (P3-05, like web/admin's auth interceptor). On a 401 it
 * retries with a newer token if another call already refreshed, otherwise refreshes once and retries. Only a
 * rejected refresh cookie ends the session; a network or server error during refresh goes to the caller.
 */
export async function authFetch(request: Request): Promise<Response> {
  const path = new URL(request.url).pathname;
  if (!path.startsWith('/api/') || path.startsWith('/api/v1/auth/')) {
    return fetch(request);
  }
  const retry = request.clone();
  const sentToken = session.token();
  const response = await fetch(withToken(request, sentToken));
  if (response.status !== 401 || !(await isExpiredToken(response))) {
    return response;
  }
  const current = session.token();
  if (current && current !== sentToken) {
    return fetch(withToken(retry, current)); // Another request refreshed while this one was on the wire.
  }
  // A rejected refresh ends the session as expired (Session.refresh); the caller gets the original 401.
  const token = await session.refresh();
  return token ? fetch(withToken(retry, token)) : response;
}

/**
 * Typed API client generated from openapi/spor-akademi.json (`npm run generate:api`, runs before
 * dev/build/test/lint/typecheck). Paths already start with /api/v1, so the base URL is the page's own
 * origin (same-origin requests): the Vite dev proxy and, in production, the reverse proxy route /api to the API.
 */
export const api = createClient<paths>({ baseUrl: window.location.origin, fetch: authFetch });

export type { components, paths } from './schema.gen';
