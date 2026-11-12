import { APIRequestContext, APIResponse, expect, request } from '@playwright/test';

import { apiUrl } from './urls';

interface SessionResponse {
  accessToken: string;
  user: { mustChangePassword: boolean };
}

/**
 * A thin client for arranging test data through the real API (never for the behaviour under test).
 * Calls the API directly, not through the dev-server proxy; the bearer token is kept in memory only.
 */
export class ApiClient {
  private constructor(
    private readonly context: APIRequestContext,
    private token: string,
  ) {}

  static async login(email: string, password: string): Promise<ApiClient> {
    const context = await request.newContext({ baseURL: apiUrl });
    const response = await context.post('/api/v1/auth/login', { data: { email, password } });
    await expectStatus(response, 200, 'login');
    const session = (await response.json()) as SessionResponse;
    return new ApiClient(context, session.accessToken);
  }

  /**
   * Uses the access token of a session the test already opened (e.g. through the login form), so arranging
   * data costs no extra login (rate limited, NFR-SEC-14). The token stays valid for 15 minutes.
   */
  static async withToken(accessToken: string): Promise<ApiClient> {
    return new ApiClient(await request.newContext({ baseURL: apiUrl }), accessToken);
  }

  /** Replaces a temporary password; the API answers with a fresh session, which this client keeps. */
  async changePassword(currentPassword: string, newPassword: string): Promise<void> {
    const session = await this.post<SessionResponse>(
      '/api/v1/auth/change-password',
      { currentPassword, newPassword },
      200,
    );
    this.token = session.accessToken;
  }

  get<T>(path: string): Promise<T> {
    return this.send<T>('GET', path, undefined, 200);
  }

  post<T>(
    path: string,
    body: unknown,
    status = 201,
    headers: Record<string, string> = {},
  ): Promise<T> {
    return this.send<T>('POST', path, body, status, headers);
  }

  put<T>(path: string, body: unknown, status = 200): Promise<T> {
    return this.send<T>('PUT', path, body, status);
  }

  async dispose(): Promise<void> {
    await this.context.dispose();
  }

  private async send<T>(
    method: string,
    path: string,
    body: unknown,
    status: number,
    headers: Record<string, string> = {},
  ): Promise<T> {
    const response = await this.context.fetch(path, {
      method,
      data: body,
      headers: { ...headers, Authorization: `Bearer ${this.token}` },
    });
    await expectStatus(response, status, `${method} ${path}`);
    return (await response.json()) as T;
  }
}

/** Fails with the problem body (error codes only, the API never returns personal data in it). */
async function expectStatus(response: APIResponse, status: number, what: string): Promise<void> {
  if (response.status() !== status) {
    const body = await response.text();
    expect(response.status(), `${what} answered ${response.status()}: ${body}`).toBe(status);
  }
}
