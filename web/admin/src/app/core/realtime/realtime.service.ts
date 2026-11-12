import {
  DestroyRef,
  Injectable,
  InjectionToken,
  Injector,
  effect,
  inject,
  signal,
  untracked,
} from '@angular/core';
import type { HubConnection } from '@microsoft/signalr';
import { Observable, Subject, filter, firstValueFrom, map } from 'rxjs';

import { AuthService, CurrentUser } from '../auth/auth.service';
import { ui } from '../i18n/tr';
import { ToastService } from '../../shared/ui/toast.service';

/** Hub on the API's origin (proxied in development, P3-08). */
export const HUB_URL = '/hubs/notifications';

/** SignalR's own reconnect schedule after a dropped connection; then it gives up and closes. */
export const RECONNECT_DELAYS_MS = [0, 2_000, 5_000, 10_000, 30_000];

/**
 * Restart schedule once the connection has finally closed or a start failed while the user is
 * still signed in. The last delay repeats until the session ends.
 */
export const RESTART_DELAYS_MS = [5_000, 10_000, 30_000];

/** A token this close to its expiry is refreshed before a (re)connect. */
export const TOKEN_REFRESH_MARGIN_MS = 30_000;

export type NotificationType =
  'announcement.published' | 'attendance.recorded' | 'payment.recorded';

/** The hub's `notify` payload: ids only; screens refetch through REST. */
export interface RealtimeNotification {
  type: NotificationType;
  id: string;
  groupId: string | null;
  studentId: string | null;
  date: string | null;
  status: string | null;
}

/** A notification, or "resync": events may have been missed while disconnected, so reload. */
export type RealtimeEvent =
  { kind: 'notification'; notification: RealtimeNotification } | { kind: 'resync' };

export type RealtimeState = 'disconnected' | 'connecting' | 'connected' | 'reconnecting';

/** The part of `@microsoft/signalr` this service uses; replaced in tests. */
export type SignalRModule = Pick<
  typeof import('@microsoft/signalr'),
  'HubConnectionBuilder' | 'LogLevel'
>;

/** Loads the SignalR client on first use, so it stays out of the initial bundle. */
export const SIGNALR_LOADER = new InjectionToken<() => Promise<SignalRModule>>('SIGNALR_LOADER', {
  providedIn: 'root',
  factory: () => () => import('@microsoft/signalr'),
});

const KNOWN_TYPES: readonly string[] = [
  'announcement.published',
  'attendance.recorded',
  'payment.recorded',
];

/**
 * Real-time notifications (P3-08). Connects to the hub while a School Admin or Coach is signed in
 * (not a Platform Admin, not before a required password change) and disconnects on sign-out. The
 * access token is read again on every (re)connect, refreshed when it is missing or about to expire;
 * the server closes the connection when the token expires and SignalR reconnects with a fresh one.
 * After SignalR gives up, the connection is restarted with backoff until the session ends. Every
 * successful reconnect emits "resync" so open screens reload what they might have missed.
 */
@Injectable({ providedIn: 'root' })
export class RealtimeService {
  private readonly auth = inject(AuthService);
  private readonly toasts = inject(ToastService);
  private readonly loadSignalR = inject(SIGNALR_LOADER);
  private readonly injector = inject(Injector);

  private readonly eventSubject = new Subject<RealtimeEvent>();
  private readonly connectionState = signal<RealtimeState>('disconnected');

  /** "<user id>:<role>" of the session the connection belongs to, or null when not connecting. */
  private sessionKey: string | null = null;
  private role: CurrentUser['role'] | null = null;
  /** Bumped on every stop; callbacks of an older connection are ignored. */
  private generation = 0;
  private connection: HubConnection | null = null;
  private restartTimer: ReturnType<typeof setTimeout> | null = null;
  private restartAttempt = 0;
  private hasConnected = false;
  /** Set after the server rejected the token (401): the next connect refreshes it first. */
  private forceRefresh = false;
  private bound = false;

  readonly events: Observable<RealtimeEvent> = this.eventSubject.asObservable();
  readonly state = this.connectionState.asReadonly();

  constructor() {
    inject(DestroyRef).onDestroy(() => this.stop());
  }

  /** Follows the signed-in user from now on; called once at startup. */
  connectWhileSignedIn(): void {
    if (this.bound) {
      return;
    }
    this.bound = true;
    effect(
      () => {
        const user = this.auth.user();
        untracked(() => this.follow(user));
      },
      { injector: this.injector },
    );
  }

  /** Emits when announcements may have changed: one was published (to `groupId`, if given) or a resync. */
  announcementsChanged(groupId?: () => string): Observable<void> {
    return this.events.pipe(
      filter(
        (event) =>
          event.kind === 'resync' ||
          (event.notification.type === 'announcement.published' &&
            (!groupId || event.notification.groupId === groupId())),
      ),
      map(() => undefined),
    );
  }

  private follow(user: CurrentUser | null): void {
    const eligible =
      !!user && !user.mustChangePassword && (user.role === 'SchoolAdmin' || user.role === 'Coach');
    const key = eligible ? `${user.id}:${user.role}` : null;
    if (key === this.sessionKey) {
      return;
    }
    this.stop();
    this.sessionKey = key;
    this.role = eligible ? user.role : null;
    if (key) {
      void this.connect();
    }
  }

  private stop(): void {
    this.generation++;
    if (this.restartTimer) {
      clearTimeout(this.restartTimer);
      this.restartTimer = null;
    }
    this.restartAttempt = 0;
    this.hasConnected = false;
    this.forceRefresh = false;
    const connection = this.connection;
    this.connection = null;
    this.connectionState.set('disconnected');
    if (connection) {
      void connection.stop().catch(() => undefined);
    }
  }

  private async connect(): Promise<void> {
    const generation = this.generation;
    this.connectionState.set('connecting');
    let signalR: SignalRModule;
    try {
      signalR = await this.loadSignalR();
    } catch {
      if (generation === this.generation) {
        this.scheduleRestart();
      }
      return;
    }
    if (generation !== this.generation) {
      return;
    }

    const connection = new signalR.HubConnectionBuilder()
      .withUrl(HUB_URL, { accessTokenFactory: () => this.freshToken() })
      .withAutomaticReconnect(RECONNECT_DELAYS_MS)
      // No console output: messages can carry URLs and server details.
      .configureLogging(signalR.LogLevel.None)
      .build();
    this.connection = connection;

    connection.on('notify', (payload: unknown) => {
      if (generation === this.generation) {
        this.receive(payload);
      }
    });
    connection.onreconnecting(() => {
      if (generation === this.generation) {
        this.connectionState.set('reconnecting');
      }
    });
    connection.onreconnected(() => {
      if (generation === this.generation) {
        this.connectionState.set('connected');
        this.eventSubject.next({ kind: 'resync' });
      }
    });
    connection.onclose(() => {
      if (generation === this.generation && this.connection === connection) {
        this.connection = null;
        this.scheduleRestart();
      }
    });

    try {
      await connection.start();
    } catch (error) {
      if (generation !== this.generation) {
        return;
      }
      this.connection = null;
      const status = statusOf(error);
      if (status === 403) {
        // The server will not take this session (role or pending password change): do not retry.
        this.connectionState.set('disconnected');
        return;
      }
      this.forceRefresh = status === 401;
      this.scheduleRestart();
      return;
    }
    if (generation !== this.generation) {
      void connection.stop().catch(() => undefined);
      return;
    }
    this.restartAttempt = 0;
    this.connectionState.set('connected');
    if (this.hasConnected) {
      this.eventSubject.next({ kind: 'resync' });
    }
    this.hasConnected = true;
  }

  private scheduleRestart(): void {
    this.connectionState.set('disconnected');
    if (this.restartTimer || !this.sessionKey) {
      return;
    }
    // Events may be missed until the next start; that start emits "resync".
    this.hasConnected = true;
    const delay = RESTART_DELAYS_MS[Math.min(this.restartAttempt, RESTART_DELAYS_MS.length - 1)];
    this.restartAttempt++;
    const generation = this.generation;
    this.restartTimer = setTimeout(() => {
      this.restartTimer = null;
      if (generation === this.generation) {
        void this.connect();
      }
    }, delay);
  }

  /** Called by SignalR before every (re)connect. Throws when the session is over. */
  private async freshToken(): Promise<string> {
    const token = this.auth.token();
    if (token && !this.forceRefresh && !expiresSoon(token, Date.now())) {
      return token;
    }
    this.forceRefresh = false;
    const refreshed = await firstValueFrom(this.auth.refresh());
    if (!refreshed) {
      // Signed out: the session effect stops the connection.
      throw new Error('Session ended.');
    }
    return refreshed;
  }

  private receive(payload: unknown): void {
    const notification = parseNotification(payload);
    if (!notification) {
      return;
    }
    this.eventSubject.next({ kind: 'notification', notification });
    // A School Admin has just published it themselves; their screens only reload.
    if (notification.type === 'announcement.published' && this.role === 'Coach') {
      this.toasts.info(ui.announcements.newPublished);
    }
  }
}

function parseNotification(payload: unknown): RealtimeNotification | null {
  if (typeof payload !== 'object' || payload === null) {
    return null;
  }
  const value = payload as Record<string, unknown>;
  const type = value['type'];
  const id = value['id'];
  if (typeof type !== 'string' || !KNOWN_TYPES.includes(type) || typeof id !== 'string') {
    return null;
  }
  const text = (key: string) => (typeof value[key] === 'string' ? (value[key] as string) : null);
  return {
    type: type as NotificationType,
    id,
    groupId: text('groupId'),
    studentId: text('studentId'),
    date: text('date'),
    status: text('status'),
  };
}

/** True when the JWT's `exp` is within the margin; an unreadable token is used as it is. */
export function expiresSoon(token: string, now: number): boolean {
  try {
    const part = token.split('.')[1] ?? '';
    const base64 = part.replace(/-/g, '+').replace(/_/g, '/');
    const payload = JSON.parse(atob(base64.padEnd(Math.ceil(base64.length / 4) * 4, '='))) as {
      exp?: unknown;
    };
    return typeof payload.exp === 'number' && payload.exp * 1000 - now < TOKEN_REFRESH_MARGIN_MS;
  } catch {
    return false;
  }
}

/** HTTP status of a failed negotiate (SignalR's HttpError), if any. */
function statusOf(error: unknown): number | null {
  const status = (error as { statusCode?: unknown } | null)?.statusCode;
  return typeof status === 'number' ? status : null;
}
