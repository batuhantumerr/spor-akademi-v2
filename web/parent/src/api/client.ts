import createClient from 'openapi-fetch';
import type { paths } from './schema.gen';

/**
 * Typed API client generated from openapi/spor-akademi.json (`npm run generate:api`, runs before
 * dev/build/test/lint/typecheck). Paths already start with /api/v1, so the base URL is the page's own
 * origin (same-origin requests): the Vite dev proxy and, in production, the reverse proxy route /api to the API.
 * Authentication (in-memory access token, refresh cookie) is added in P3-05.
 */
export const api = createClient<paths>({ baseUrl: window.location.origin });

export type { components, paths } from './schema.gen';
