import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Observable, of } from 'rxjs';

import { GroupDto } from '../../api/models';
import { CoachAnnouncementsComponent } from '../../features/announcements/coach-announcements.component';
import { GroupAnnouncementsComponent } from '../../features/announcements/group-announcements.component';
import { ToastService } from '../../shared/ui/toast.service';
import { AuthService, CurrentUser, Role } from '../auth/auth.service';
import { ui } from '../i18n/tr';
import {
  HUB_URL,
  RECONNECT_DELAYS_MS,
  RESTART_DELAYS_MS,
  RealtimeEvent,
  RealtimeService,
  SIGNALR_LOADER,
  SignalRModule,
  expiresSoon,
} from './realtime.service';

type Handler = (...args: unknown[]) => void;

class FakeConnection {
  readonly handlers = new Map<string, Handler>();
  close: Handler = () => undefined;
  reconnecting: Handler = () => undefined;
  reconnected: Handler = () => undefined;
  readonly start = vi.fn(() => nextStart());
  readonly stop = vi.fn(() => Promise.resolve());

  on(name: string, handler: Handler): void {
    this.handlers.set(name, handler);
  }
  onclose(handler: Handler): void {
    this.close = handler;
  }
  onreconnecting(handler: Handler): void {
    this.reconnecting = handler;
  }
  onreconnected(handler: Handler): void {
    this.reconnected = handler;
  }
  notify(payload: unknown): void {
    this.handlers.get('notify')?.(payload);
  }
}

interface Built {
  url: string;
  accessTokenFactory: () => Promise<string>;
  delays: number[];
  connection: FakeConnection;
}

let built: Built[] = [];
let startResults: (() => Promise<void>)[] = [];
const nextStart = () => (startResults.shift() ?? (() => Promise.resolve()))();

class FakeBuilder {
  private url = '';
  private factory: () => Promise<string> = () => Promise.resolve('');
  private delays: number[] = [];

  withUrl(url: string, options: { accessTokenFactory: () => Promise<string> }): this {
    this.url = url;
    this.factory = options.accessTokenFactory;
    return this;
  }
  withAutomaticReconnect(delays: number[]): this {
    this.delays = delays;
    return this;
  }
  configureLogging(): this {
    return this;
  }
  build(): FakeConnection {
    const connection = new FakeConnection();
    built.push({
      url: this.url,
      accessTokenFactory: this.factory,
      delays: this.delays,
      connection,
    });
    return connection;
  }
}

const fakeSignalR = {
  HubConnectionBuilder: FakeBuilder,
  LogLevel: { None: 6 },
} as unknown as SignalRModule;

/** A synthetic, unsigned JWT that expires `seconds` from now. */
function jwt(seconds: number): string {
  const encode = (value: object) =>
    btoa(JSON.stringify(value)).replace(/=+$/, '').replace(/\+/g, '-').replace(/\//g, '_');
  return `${encode({ alg: 'none' })}.${encode({ exp: Math.floor(Date.now() / 1000) + seconds })}.x`;
}

function user(role: Role, overrides: Partial<CurrentUser> = {}): CurrentUser {
  return {
    id: 'u1',
    email: 'antrenor@ornek.test',
    firstName: 'Deneme',
    lastName: 'Kullanıcı',
    role,
    tenantId: 't1',
    mustChangePassword: false,
    ...overrides,
  };
}

const forbidden = () => Promise.reject(Object.assign(new Error('Forbidden'), { statusCode: 403 }));
const unreachable = () => Promise.reject(new Error('Failed to fetch'));

const announcementPublished = {
  type: 'announcement.published',
  id: 'a9',
  groupId: 'g1',
  studentId: null,
  date: null,
  status: null,
};

describe('RealtimeService', () => {
  let currentUser: ReturnType<typeof signal<CurrentUser | null>>;
  let token: string | null;
  let refresh: ReturnType<typeof vi.fn<() => Observable<string | null>>>;
  let toast: { info: ReturnType<typeof vi.fn> };
  let service: RealtimeService;
  let events: RealtimeEvent[];

  /** Runs the session effect and lets the loader and start() promises settle. */
  async function settle(ms = 0): Promise<void> {
    TestBed.tick();
    await vi.advanceTimersByTimeAsync(ms);
  }

  beforeEach(() => {
    vi.useFakeTimers();
    built = [];
    startResults = [];
    currentUser = signal<CurrentUser | null>(null);
    token = jwt(900);
    refresh = vi.fn(() => of<string | null>(jwt(900)));
    toast = { info: vi.fn() };
    TestBed.configureTestingModule({
      providers: [
        {
          provide: AuthService,
          useValue: { user: currentUser, token: () => token, refresh },
        },
        { provide: ToastService, useValue: toast },
        { provide: SIGNALR_LOADER, useValue: () => Promise.resolve(fakeSignalR) },
      ],
    });
    service = TestBed.inject(RealtimeService);
    events = [];
    service.events.subscribe((event) => events.push(event));
    service.connectWhileSignedIn();
  });

  afterEach(() => {
    TestBed.resetTestingModule();
    vi.useRealTimers();
    delete (document as unknown as Record<string, unknown>)['visibilityState'];
  });

  function setVisibility(state: DocumentVisibilityState): void {
    Object.defineProperty(document, 'visibilityState', { configurable: true, get: () => state });
    document.dispatchEvent(new Event('visibilitychange'));
  }

  it.each<Role>(['SchoolAdmin', 'Coach'])('connects after a %s signs in', async (role) => {
    await settle();
    expect(built.length).toBe(0);

    currentUser.set(user(role));
    await settle();

    expect(built.length).toBe(1);
    expect(built[0].url).toBe(HUB_URL);
    expect(built[0].delays).toEqual(RECONNECT_DELAYS_MS);
    expect(built[0].connection.start).toHaveBeenCalledTimes(1);
    expect(service.state()).toBe('connected');
    // The first connect of a session needs no resync: the screens have just loaded.
    expect(events).toEqual([]);
  });

  it('does not connect for a Platform Admin', async () => {
    currentUser.set(user('PlatformAdmin', { tenantId: null }));
    await settle(60_000);
    expect(built.length).toBe(0);
  });

  it('waits for a required password change, then connects', async () => {
    currentUser.set(user('Coach', { mustChangePassword: true }));
    await settle(60_000);
    expect(built.length).toBe(0);

    currentUser.set(user('Coach'));
    await settle();
    expect(built.length).toBe(1);
  });

  it('does not reconnect when the same user is loaded again', async () => {
    currentUser.set(user('Coach'));
    await settle();
    currentUser.set(user('Coach'));
    await settle();
    expect(built.length).toBe(1);
  });

  it('gives SignalR the current token, and a fresh one when it is missing or expiring', async () => {
    currentUser.set(user('Coach'));
    await settle();
    const factory = built[0].accessTokenFactory;

    const current = token;
    await expect(factory()).resolves.toBe(current);
    expect(refresh).not.toHaveBeenCalled();

    token = jwt(10);
    const renewed = jwt(900);
    refresh.mockReturnValueOnce(of(renewed));
    await expect(factory()).resolves.toBe(renewed);

    token = null;
    refresh.mockReturnValueOnce(of('next-token'));
    await expect(factory()).resolves.toBe('next-token');
    expect(refresh).toHaveBeenCalledTimes(2);
  });

  it('defers a needed refresh while the tab is hidden', async () => {
    currentUser.set(user('Coach'));
    await settle();
    setVisibility('hidden');
    token = jwt(10);
    let result: string | undefined;
    void built[0].accessTokenFactory().then((t) => (result = t));
    await settle(60_000);
    expect(refresh).not.toHaveBeenCalled();
    expect(result).toBeUndefined();

    setVisibility('visible');
    await settle();
    expect(refresh).toHaveBeenCalledTimes(1);
    expect(result).toBeDefined();
  });

  it('uses a token renewed elsewhere while the tab was hidden, without refreshing', async () => {
    currentUser.set(user('Coach'));
    await settle();
    setVisibility('hidden');
    token = null;
    const pending = built[0].accessTokenFactory();
    await settle();
    token = jwt(900);
    setVisibility('visible');
    await expect(pending).resolves.toBe(token);
    expect(refresh).not.toHaveBeenCalled();
  });

  it('still returns a fresh token from a hidden tab without refreshing', async () => {
    currentUser.set(user('Coach'));
    await settle();
    setVisibility('hidden');
    await expect(built[0].accessTokenFactory()).resolves.toBe(token);
    expect(refresh).not.toHaveBeenCalled();
  });

  it('gives up waiting for visibility when the session ends', async () => {
    currentUser.set(user('Coach'));
    await settle();
    setVisibility('hidden');
    token = null;
    const pending = built[0].accessTokenFactory();
    const outcome = expect(pending).rejects.toThrow();
    currentUser.set(null);
    await settle();
    await outcome;
    setVisibility('visible');
    await settle();
    expect(refresh).not.toHaveBeenCalled();
  });

  it('fails the token request when the refresh says the session is over', async () => {
    currentUser.set(user('Coach'));
    await settle();
    token = null;
    refresh.mockReturnValueOnce(of(null));
    await expect(built[0].accessTokenFactory()).rejects.toThrow();
  });

  it('disconnects on logout and does not restart', async () => {
    currentUser.set(user('SchoolAdmin'));
    await settle();
    const connection = built[0].connection;

    currentUser.set(null);
    await settle();
    expect(connection.stop).toHaveBeenCalled();
    expect(service.state()).toBe('disconnected');

    connection.close();
    await settle(120_000);
    expect(built.length).toBe(1);
  });

  it('restarts with backoff after the connection finally closes, then resyncs', async () => {
    currentUser.set(user('Coach'));
    await settle();

    startResults = [unreachable];
    built[0].connection.close(new Error('Reconnect gave up'));
    await settle(RESTART_DELAYS_MS[0] - 1);
    expect(built.length).toBe(1);
    await settle(1);
    expect(built.length).toBe(2);
    expect(events).toEqual([]);

    // The restart failed; the next one waits longer and succeeds.
    await settle(RESTART_DELAYS_MS[1] - 1);
    expect(built.length).toBe(2);
    await settle(1);
    expect(built.length).toBe(3);
    expect(service.state()).toBe('connected');
    expect(events).toEqual([{ kind: 'resync' }]);
  });

  it('keeps retrying a failed first start every 30 seconds at most', async () => {
    startResults = [unreachable, unreachable, unreachable, unreachable];
    currentUser.set(user('Coach'));
    await settle();
    await settle(RESTART_DELAYS_MS[0]);
    await settle(RESTART_DELAYS_MS[1]);
    await settle(RESTART_DELAYS_MS[2]);
    expect(built.length).toBe(4);
    await settle(RESTART_DELAYS_MS[2]);
    expect(built.length).toBe(5);
    expect(service.state()).toBe('connected');
    expect(events).toEqual([{ kind: 'resync' }]);
  });

  it('stops retrying when the server refuses the session (403)', async () => {
    startResults = [forbidden];
    currentUser.set(user('SchoolAdmin'));
    await settle(120_000);
    expect(built.length).toBe(1);
    expect(service.state()).toBe('disconnected');
  });

  it('refreshes the token before the next attempt after a 401', async () => {
    startResults = [
      () => Promise.reject(Object.assign(new Error('Unauthorized'), { statusCode: 401 })),
    ];
    currentUser.set(user('Coach'));
    await settle();
    await settle(RESTART_DELAYS_MS[0]);
    expect(built.length).toBe(2);
    await built[1].accessTokenFactory();
    expect(refresh).toHaveBeenCalledTimes(1);
  });

  it('emits resync when SignalR reconnects on its own', async () => {
    currentUser.set(user('SchoolAdmin'));
    await settle();
    built[0].connection.reconnecting();
    expect(service.state()).toBe('reconnecting');
    built[0].connection.reconnected('conn-2');
    expect(service.state()).toBe('connected');
    expect(events).toEqual([{ kind: 'resync' }]);
  });

  it('passes known notifications on and ignores unknown or malformed ones', async () => {
    currentUser.set(user('SchoolAdmin'));
    await settle();
    const connection = built[0].connection;
    connection.notify({ type: 'something.else', id: 'x' });
    connection.notify('not an object');
    connection.notify({ type: 'announcement.published' });
    connection.notify(announcementPublished);

    expect(events).toEqual([{ kind: 'notification', notification: announcementPublished }]);
    // The admin published it themselves: no toast, the list just reloads.
    expect(toast.info).not.toHaveBeenCalled();
  });

  it('tells a coach that a new announcement was published', async () => {
    currentUser.set(user('Coach'));
    await settle();
    built[0].connection.notify(announcementPublished);
    expect(toast.info).toHaveBeenCalledWith(ui.announcements.newPublished);
  });

  it('ignores messages from a connection that belonged to an ended session', async () => {
    currentUser.set(user('Coach'));
    await settle();
    const old = built[0].connection;
    currentUser.set(null);
    await settle();
    old.notify(announcementPublished);
    old.reconnected();
    expect(events).toEqual([]);
  });
});

describe('expiresSoon', () => {
  it('reads the exp claim and treats unreadable tokens as usable', () => {
    expect(expiresSoon(jwt(900), Date.now())).toBe(false);
    expect(expiresSoon(jwt(5), Date.now())).toBe(true);
    expect(expiresSoon(jwt(-5), Date.now())).toBe(true);
    expect(expiresSoon('not-a-jwt', Date.now())).toBe(false);
  });
});

describe('screens on real-time events', () => {
  let backend: HttpTestingController;
  let connection: () => FakeConnection;
  const currentUser = signal<CurrentUser | null>(null);
  const emptyPage = { items: [], page: 1, pageSize: 10, totalCount: 0, totalPages: 0 };

  async function settle(): Promise<void> {
    TestBed.tick();
    await new Promise((resolve) => setTimeout(resolve));
    TestBed.tick();
  }

  async function signIn(role: Role): Promise<void> {
    currentUser.set(user(role));
    TestBed.inject(RealtimeService).connectWhileSignedIn();
    await settle();
    await settle();
  }

  beforeEach(() => {
    built = [];
    startResults = [];
    currentUser.set(null);
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        {
          provide: AuthService,
          useValue: { user: currentUser, token: () => jwt(900), refresh: () => of(jwt(900)) },
        },
        { provide: SIGNALR_LOADER, useValue: () => Promise.resolve(fakeSignalR) },
      ],
    });
    backend = TestBed.inject(HttpTestingController);
    connection = () => built[built.length - 1].connection;
  });

  afterEach(() => {
    backend.verify();
    for (const toast of TestBed.inject(ToastService).toasts()) {
      TestBed.inject(ToastService).dismiss(toast.id);
    }
  });

  it("reloads the coach's announcements when one is published, and after a resync", async () => {
    await signIn('Coach');
    const fixture = TestBed.createComponent(CoachAnnouncementsComponent);
    await settle();
    backend.expectOne((r) => r.url === '/api/v1/me/announcements').flush(emptyPage);
    await settle();

    connection().notify(announcementPublished);
    await settle();
    backend.expectOne((r) => r.url === '/api/v1/me/announcements').flush(emptyPage);
    expect(
      TestBed.inject(ToastService)
        .toasts()
        .map((t) => t.message),
    ).toEqual([ui.announcements.newPublished]);

    connection().reconnected();
    await settle();
    backend.expectOne((r) => r.url === '/api/v1/me/announcements').flush(emptyPage);

    connection().notify({ ...announcementPublished, type: 'payment.recorded' });
    await settle();
    backend.expectNone((r) => r.url === '/api/v1/me/announcements');
    fixture.destroy();
  });

  it("reloads a group's announcements only for that group", async () => {
    await signIn('SchoolAdmin');
    const group = {
      id: 'g1',
      name: 'U12 Mavi',
      status: 'Active',
      slots: [],
    } as unknown as GroupDto;
    const fixture = TestBed.createComponent(GroupAnnouncementsComponent);
    fixture.componentRef.setInput('group', group);
    await settle();
    backend.expectOne((r) => r.url === '/api/v1/groups/g1/announcements').flush(emptyPage);
    await settle();

    connection().notify({ ...announcementPublished, groupId: 'g2' });
    await settle();
    backend.expectNone((r) => r.url.includes('/announcements'));

    connection().notify(announcementPublished);
    await settle();
    backend.expectOne((r) => r.url === '/api/v1/groups/g1/announcements').flush(emptyPage);
    // The admin is told nothing: they published it.
    expect(TestBed.inject(ToastService).toasts()).toEqual([]);
    fixture.destroy();
  });
});
