import { useSyncExternalStore } from 'react';
import type { components } from '../api/schema.gen';
import { ApiError, networkError, problemFromResponse } from '../api/errors';

export type User = components['schemas']['UserSummary'];
type SessionResponse = components['schemas']['SessionResponse'];

/** `offline`: the start-up restore could not reach the server; nothing is known about the session yet (P3-07). */
export type SessionStatus = 'loading' | 'signedIn' | 'signedOut' | 'offline';

export interface SessionState {
  readonly status: SessionStatus;
  readonly user: User | null;
  /** True after an unrecoverable 401 ended the session (the login page says so). */
  readonly expired: boolean;
}

/** Refresh and logout carry this header (DEC-055): a cross-site form cannot set it. */
const AJAX_HEADERS = { 'X-Requested-With': 'XMLHttpRequest' };

/**
 * Session state (ADR-0006, P3-05), the parent app's version of web/admin core/auth/auth.service.ts: the access
 * token lives only in this object's memory; the refresh token is an HttpOnly cookie the browser sends to
 * /api/v1/auth. A page load restores the session with one refresh call. Only the Parent role is accepted.
 *
 * Every deliberate end of the session (logout, expiry) bumps an epoch; a refresh that started under an older
 * epoch cannot revive the session.
 */
export class Session {
  private state: SessionState = { status: 'loading', user: null, expired: false };
  private accessToken: string | null = null;
  private refreshInFlight: Promise<string | null> | null = null;
  private sessionEpoch = 0;
  private readonly listeners = new Set<() => void>();
  private readonly endListeners = new Set<() => void>();

  readonly subscribe = (listener: () => void): (() => void) => {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  };

  readonly getState = (): SessionState => this.state;

  /** Runs when a session ends (logout, expiry, refused role): cached personal data must go with it. */
  onEnd(listener: () => void): () => void {
    this.endListeners.add(listener);
    return () => this.endListeners.delete(listener);
  }

  token(): string | null {
    return this.accessToken;
  }

  epoch(): number {
    return this.sessionEpoch;
  }

  async login(email: string, password: string): Promise<User> {
    const response = await send('/api/v1/auth/login', {
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ email, password }),
    });
    if (!response.ok) {
      throw await problemFromResponse(response);
    }
    const body = (await response.json()) as SessionResponse;
    const refusal = refusalFor(body.user);
    if (refusal) {
      // The API issued a refresh cookie; drop it so the refused session cannot be restored.
      await this.endOnServer();
      throw new ApiError(refusal, 403);
    }
    this.adopt(body.accessToken, body.user);
    return body.user;
  }

  /**
   * Restores the session at start-up; ends in `signedIn`, `signedOut` or, when the network is down, `offline`
   * (the app shows "Bağlantı yok" instead of a login form that cannot work). Never throws.
   */
  async restore(): Promise<void> {
    try {
      const token = await this.refresh();
      if (!token && this.state.status === 'loading') {
        this.setState({ status: 'signedOut', user: null, expired: false });
      }
    } catch (error) {
      if (this.state.status === 'loading') {
        // Only doRefresh's failed send is `Network.Unavailable`; any other failure (a server error, a malformed
        // answer, a bug) means no usable session, not "no network".
        const offline = error instanceof ApiError && error.code === networkError().code;
        this.setState({ status: offline ? 'offline' : 'signedOut', user: null, expired: false });
      }
    }
  }

  /** Tries the start-up restore again after it found no network (retry button, the browser's `online` event). */
  retryRestore(): Promise<void> {
    if (this.state.status !== 'offline') return Promise.resolve();
    this.setState({ status: 'loading', user: null, expired: false });
    return this.restore();
  }

  /**
   * One refresh at a time; concurrent callers share it. Resolves to the new token, or null when the refresh
   * cookie was rejected (401/403: the session is over). Network and server errors are thrown without touching
   * the session, so a flaky connection does not sign the parent out.
   */
  refresh(): Promise<string | null> {
    if (this.refreshInFlight) return this.refreshInFlight;
    const call: Promise<string | null> = this.doRefresh().finally(() => {
      // A logout may have dropped this call and a newer refresh may have started meanwhile; keep that one.
      if (this.refreshInFlight === call) this.refreshInFlight = null;
    });
    this.refreshInFlight = call;
    return call;
  }

  /** Ends the session here first (so an in-flight refresh cannot bring it back), then on the server. */
  async logout(): Promise<void> {
    this.end(false);
    await this.endOnServer();
  }

  /** After an unrecoverable 401: the session is gone; the login page tells the parent why. */
  expire(): void {
    this.end(true);
  }

  /** The state a fresh page would have after a failed restore (tests, and after a password change elsewhere). */
  forget(): void {
    this.end(false);
  }

  /** Accepts a token and its user (login, refresh, tests). */
  adopt(accessToken: string, user: User): void {
    this.accessToken = accessToken;
    this.setState({ status: 'signedIn', user, expired: false });
  }

  private async doRefresh(): Promise<string | null> {
    const epoch = this.sessionEpoch;
    let response: Response;
    try {
      response = await send('/api/v1/auth/refresh', { headers: AJAX_HEADERS });
    } catch {
      // fetch itself rejected: the request never got an answer. The only path that means "offline".
      throw networkError();
    }
    if (response.status === 401 || response.status === 403) {
      // Rejected while signed in: the session expired. At start-up there simply was none.
      if (epoch === this.sessionEpoch) this.end(this.state.status === 'signedIn');
      return null;
    }
    if (!response.ok) {
      throw await problemFromResponse(response);
    }
    const body = await sessionBody(response);
    if (epoch !== this.sessionEpoch) {
      return null; // Signed out while the refresh was on the wire.
    }
    if (refusalFor(body.user)) {
      this.end(false);
      await this.endOnServer();
      return null;
    }
    this.adopt(body.accessToken, body.user);
    return body.accessToken;
  }

  private end(expired: boolean): void {
    this.sessionEpoch++;
    this.refreshInFlight = null;
    this.accessToken = null;
    this.setState({ status: 'signedOut', user: null, expired });
    for (const listener of this.endListeners) listener();
  }

  private async endOnServer(): Promise<void> {
    try {
      await send('/api/v1/auth/logout', { headers: AJAX_HEADERS });
    } catch {
      // Offline: the cookie expires on its own; the local session is already gone.
    }
  }

  private setState(state: SessionState): void {
    this.state = state;
    for (const listener of this.listeners) listener();
  }
}

/** A 200 refresh answer's body; anything that is not a session (bad JSON, no token or user) is an unexpected error. */
async function sessionBody(response: Response): Promise<SessionResponse> {
  let body: unknown;
  try {
    body = await response.json();
  } catch {
    throw new ApiError('Unexpected.Error', response.status);
  }
  const candidate = (typeof body === 'object' && body !== null ? body : {}) as Partial<SessionResponse>;
  if (
    typeof candidate.accessToken !== 'string' ||
    typeof candidate.user !== 'object' ||
    (candidate.user as unknown) === null
  ) {
    throw new ApiError('Unexpected.Error', response.status);
  }
  return candidate as SessionResponse;
}

/** Why a session must not be used in the parent app, or null when it may. */
function refusalFor(user: User): string | null {
  if (user.role !== 'Parent') return 'ParentApp.NotParent';
  // An invited or re-invited account sees no children until the invitation link sets a password (DEC-384).
  if (user.mustChangePassword) return 'ParentApp.PasswordSetupRequired';
  return null;
}

function send(path: string, init: RequestInit): Promise<Response> {
  return fetch(new URL(path, window.location.origin), {
    ...init,
    method: 'POST',
    credentials: 'same-origin',
  });
}

export const session = new Session();

export function useSession(): SessionState {
  return useSyncExternalStore(session.subscribe, session.getState);
}
