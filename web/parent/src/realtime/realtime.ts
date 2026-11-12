import { HubConnectionBuilder, LogLevel, type HubConnection, type RetryContext } from '@microsoft/signalr';
import type { QueryClient } from '@tanstack/react-query';
import type { Session } from '../auth/session';
import { schoolNow } from '../format/school-time';
import {
  childFirstName,
  invalidateAfterConnect,
  invalidateFor,
  notificationMessage,
  parseNotification,
} from './notifications';
import { toasts as defaultToasts, type ToastStore } from './toasts';

/** Same origin as the API (Vite dev proxy, reverse proxy in production). */
export const HUB_PATH = '/hubs/notifications';
/** The only server-to-client method; the app never invokes a hub method. */
export const NOTIFY_METHOD = 'notify';

/** SignalR's own reconnect after a dropped connection: right away, then 2, 5, 10 and 30 seconds. */
export const RECONNECT_DELAYS_MS: readonly number[] = [0, 2_000, 5_000, 10_000, 30_000];
/**
 * After SignalR gave up (or the first start failed, or the server closed the connection, e.g. when the access token
 * expired) while still signed in: a new connection after 1, 5, 15, 30, then every 60 seconds.
 */
export const RESTART_DELAYS_MS: readonly number[] = [1_000, 5_000, 15_000, 30_000, 60_000];
/** A connection that stayed up this long resets the restart backoff. */
export const STABLE_CONNECTION_MS = 30_000;
/** A token that expires within this window is refreshed before it is handed to the hub. */
const TOKEN_EXPIRY_SKEW_MS = 30_000;

export interface RealtimeOptions {
  readonly session: Session;
  readonly queryClient: QueryClient;
  readonly toasts?: ToastStore;
  readonly origin?: string;
  readonly now?: () => number;
  /** 0..1; spreads restarts of many parents after a server restart (±20%). */
  readonly random?: () => number;
}

/**
 * The parent app's notifications hub connection (P3-08), tied to the session: connects when a parent is signed in
 * (login or restore), disconnects on logout or expiry. Only a Parent session without a pending password setup ever
 * reaches `signedIn` (Session refuses the rest), and that is checked again here.
 *
 * Every successful (re)connect refetches what a notification could have changed, because events sent while
 * disconnected are lost. A notification refetches its queries and shows a toast built from the app's own data.
 */
export class Realtime {
  private readonly session: Session;
  private readonly queryClient: QueryClient;
  private readonly toasts: ToastStore;
  private readonly hubUrl: string;
  private readonly now: () => number;
  private readonly random: () => number;

  private connection: HubConnection | null = null;
  /** Session epoch the current connection belongs to; a new session gets a new connection. */
  private connectionEpoch = -1;
  private connectedAt: number | null = null;
  private restartAttempt = 0;
  private restartTimer: ReturnType<typeof setTimeout> | null = null;
  /** Session epoch whose negotiate was refused (403): no more attempts until a new session. */
  private refusedEpoch: number | null = null;
  private disposers: (() => void)[] = [];

  constructor(options: RealtimeOptions) {
    this.session = options.session;
    this.queryClient = options.queryClient;
    this.toasts = options.toasts ?? defaultToasts;
    this.hubUrl = new URL(HUB_PATH, options.origin ?? window.location.origin).toString();
    this.now = options.now ?? Date.now;
    this.random = options.random ?? Math.random;
  }

  /** Follows the session from now on; returns the function that stops everything. */
  start(): () => void {
    const onSession = () => {
      this.sync();
    };
    const onOnline = () => {
      // Back online: try now instead of waiting out the backoff.
      if (this.restartTimer !== null) {
        this.clearRestart();
        this.connect();
      }
    };
    this.disposers.push(this.session.subscribe(onSession));
    window.addEventListener('online', onOnline);
    this.disposers.push(() => {
      window.removeEventListener('online', onOnline);
    });
    this.sync();
    return () => {
      for (const dispose of this.disposers) dispose();
      this.disposers = [];
      this.disconnect();
    };
  }

  /** True while the session may hold a hub connection. */
  private wanted(epoch = this.connectionEpoch): boolean {
    const { status, user } = this.session.getState();
    return (
      status === 'signedIn' &&
      user !== null &&
      user.role === 'Parent' &&
      !user.mustChangePassword &&
      this.session.epoch() === epoch
    );
  }

  private sync(): void {
    const epoch = this.session.epoch();
    if (!this.wanted(epoch)) {
      this.disconnect();
      return;
    }
    if (epoch === this.refusedEpoch) return;
    // A token refresh re-announces `signedIn` for the same session; only a new session needs a new connection.
    if (epoch === this.connectionEpoch && (this.connection !== null || this.restartTimer !== null)) return;
    this.disconnect();
    this.connectionEpoch = epoch;
    this.restartAttempt = 0;
    this.connect();
  }

  private connect(): void {
    const epoch = this.connectionEpoch;
    const connection = new HubConnectionBuilder()
      .withUrl(this.hubUrl, { accessTokenFactory: () => this.accessToken(epoch) })
      .withAutomaticReconnect({
        nextRetryDelayInMilliseconds: (context: RetryContext) => {
          if (isForbidden(context.retryReason)) {
            this.refusedEpoch = epoch; // SignalR then closes; restartLater sees the refusal.
            return null;
          }
          return this.wanted(epoch) ? (RECONNECT_DELAYS_MS[context.previousRetryCount] ?? null) : null;
        },
      })
      // No console output: URLs and messages may carry tokens and ids.
      .configureLogging(LogLevel.None)
      .build();

    connection.on(NOTIFY_METHOD, (payload: unknown) => {
      if (this.connection === connection) this.handle(payload);
    });
    connection.onreconnected(() => {
      if (this.connection !== connection) return;
      this.connectedAt = this.now();
      void invalidateAfterConnect(this.queryClient);
    });
    connection.onclose((error) => {
      if (this.connection !== connection) return; // Stopped on purpose.
      this.connection = null;
      this.restartLater(error);
    });

    this.connection = connection;
    this.connectedAt = null;
    connection.start().then(
      () => {
        if (this.connection !== connection) {
          void connection.stop(); // Signed out while connecting.
          return;
        }
        this.connectedAt = this.now();
        void invalidateAfterConnect(this.queryClient);
      },
      (error: unknown) => {
        if (this.connection !== connection) return;
        this.connection = null;
        this.restartLater(error);
      },
    );
  }

  /**
   * The current access token, or a fresh one when it is missing or about to expire (reconnects after the server
   * closed an expired connection). A rejected refresh ends the session, and the session listener stops here.
   */
  private async accessToken(epoch: number): Promise<string> {
    if (!this.wanted(epoch)) throw new Error('Not signed in.');
    const token = this.session.token();
    if (token && !expiresSoon(token, this.now())) return token;
    // A hidden tab does not refresh (and rotate the shared cookie) just to reconnect; it waits until it is seen.
    await this.whenVisible(epoch);
    const fresh = await this.session.refresh();
    if (!fresh || !this.wanted(epoch)) throw new Error('Not signed in.');
    return fresh;
  }

  /** Resolves when the page is visible; rejects if the session ends while waiting. */
  private whenVisible(epoch: number): Promise<void> {
    if (document.visibilityState !== 'hidden') return Promise.resolve();
    return new Promise((resolve, reject) => {
      const cleanup = () => {
        document.removeEventListener('visibilitychange', onVisibility);
        unsubscribe();
      };
      const onVisibility = () => {
        if (document.visibilityState === 'hidden') return;
        cleanup();
        resolve();
      };
      const unsubscribe = this.session.subscribe(() => {
        if (this.wanted(epoch)) return;
        cleanup();
        reject(new Error('Not signed in.'));
      });
      document.addEventListener('visibilitychange', onVisibility);
    });
  }

  private restartLater(error?: unknown): void {
    if (isForbidden(error)) {
      // The server refuses this session (e.g. a password change is pending): no retry loop; a new sign-in may.
      this.refusedEpoch = this.connectionEpoch;
    }
    if (this.refusedEpoch === this.connectionEpoch) return;
    if (!this.wanted() || this.restartTimer !== null) return;
    if (this.connectedAt !== null && this.now() - this.connectedAt >= STABLE_CONNECTION_MS) {
      this.restartAttempt = 0;
    }
    this.connectedAt = null;
    const base = RESTART_DELAYS_MS[Math.min(this.restartAttempt, RESTART_DELAYS_MS.length - 1)] ?? 60_000;
    this.restartAttempt++;
    const delay = Math.round(base * (0.8 + 0.4 * this.random()));
    this.restartTimer = setTimeout(() => {
      this.restartTimer = null;
      if (this.wanted() && this.connection === null) this.connect();
    }, delay);
  }

  private clearRestart(): void {
    if (this.restartTimer !== null) clearTimeout(this.restartTimer);
    this.restartTimer = null;
  }

  private disconnect(): void {
    this.clearRestart();
    const connection = this.connection;
    this.connection = null;
    this.connectedAt = null;
    this.connectionEpoch = -1;
    this.toasts.clear(); // They name children; nothing of the session stays on screen.
    if (connection) void connection.stop().catch(() => undefined);
  }

  private handle(payload: unknown): void {
    const notification = parseNotification(payload);
    if (!notification) return;
    void invalidateFor(this.queryClient, notification);
    const user = this.session.getState().user;
    const today = schoolNow(new Date(this.now()), user?.schoolTimeZone ?? null).date;
    const name = childFirstName(this.queryClient, notification.studentId);
    this.toasts.show(notificationMessage(notification, name, today));
  }
}

/** SignalR's `HttpError` with status 403 (negotiate refused). */
function isForbidden(error: unknown): boolean {
  return typeof error === 'object' && error !== null && 'statusCode' in error && error.statusCode === 403;
}

/** True when the JWT's `exp` is past or within the skew; an unreadable token is left to the server to judge. */
export function expiresSoon(token: string, now: number): boolean {
  const payload = token.split('.')[1];
  if (!payload) return false;
  try {
    const base64 = payload.replace(/-/g, '+').replace(/_/g, '/');
    const claims = JSON.parse(atob(base64.padEnd(Math.ceil(base64.length / 4) * 4, '='))) as {
      exp?: unknown;
    };
    return typeof claims.exp === 'number' && claims.exp * 1000 - TOKEN_EXPIRY_SKEW_MS <= now;
  } catch {
    return false;
  }
}

/** Connects the app's session to the hub (main.tsx). */
export function startRealtime(session: Session, queryClient: QueryClient): () => void {
  return new Realtime({ session, queryClient }).start();
}
