import { vi } from 'vitest';
import type { components } from '../api/schema.gen';
import { session, type User } from '../auth/session';

export type Handler = (request: Request) => Response | Promise<Response>;

/** Every request the fake API saw, in order. */
export interface FakeApi {
  readonly calls: Request[];
  /** Adds or replaces a handler for `"GET /api/v1/…"` (exact path). */
  on(route: string, handler: Handler): void;
  callsTo(route: string): Request[];
}

/**
 * Replaces `fetch` with a router over exact `"METHOD /path"` keys. An unexpected call fails the test loudly
 * instead of reaching a network. All data is synthetic.
 */
export function fakeApi(routes: Record<string, Handler> = {}): FakeApi {
  const handlers = new Map(Object.entries(routes));
  const calls: Request[] = [];
  const key = (request: Request) => `${request.method} ${new URL(request.url).pathname}`;
  vi.stubGlobal(
    'fetch',
    vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
      const request = input instanceof Request && !init ? input : new Request(input, init);
      calls.push(request.clone());
      const handler = handlers.get(key(request));
      if (!handler) {
        return Promise.resolve(json({ code: 'Test.UnexpectedCall', route: key(request) }, 599));
      }
      return Promise.resolve(handler(request));
    }),
  );
  return {
    calls,
    on: (route, handler) => handlers.set(route, handler),
    callsTo: (route) => calls.filter((request) => key(request) === route),
  };
}

export function json(body: unknown, status = 200, headers: Record<string, string> = {}): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json', ...headers },
  });
}

export const problem = (status: number, code: string) => json({ status, code }, status);

export function parentUser(overrides: Partial<User> = {}): User {
  return {
    id: '0190a1b2-0000-7000-8000-00000000aa01',
    email: 'veli@example.test',
    firstName: 'Deneme',
    lastName: 'Veli',
    role: 'Parent',
    tenantId: '0190a1b2-0000-7000-8000-00000000bb01',
    mustChangePassword: false,
    schoolTimeZone: 'Europe/Istanbul',
    ...overrides,
  };
}

export function sessionResponse(
  user: User = parentUser(),
  accessToken = 'access-1',
): components['schemas']['SessionResponse'] {
  return { accessToken, expiresIn: 900, user };
}

/** Starts a test signed in, as a successful restore would. */
export function signIn(user: User = parentUser(), token = 'access-1'): void {
  session.adopt(token, user);
}

export const CHILD_ID = '0190a1b2-0000-7000-8000-0000000000c1';
export const ENROLLMENT_ID = '0190a1b2-0000-7000-8000-0000000000e1';
export const GROUP_ID = '0190a1b2-0000-7000-8000-0000000000a1';

export function child(overrides: Partial<components['schemas']['ParentChildDto']> = {}) {
  return {
    id: CHILD_ID,
    firstName: 'Ada',
    lastName: 'Deneme',
    birthDate: '2015-04-01',
    gender: 'Female',
    relationship: 'Mother',
    ...overrides,
  } satisfies components['schemas']['ParentChildDto'];
}

const money = (amount: string) => ({ amount, currency: 'TRY' });

export function due(overrides: Partial<components['schemas']['OutstandingDueDto']> = {}) {
  return {
    enrollmentId: ENROLLMENT_ID,
    studentId: CHILD_ID,
    studentFirstName: 'Ada',
    studentLastName: 'Deneme',
    groupId: GROUP_ID,
    groupName: 'U10 Yüzme',
    period: '2026-11',
    fee: money('900.00'),
    paid: money('0.00'),
    remaining: money('900.00'),
    listFee: money('1000.00'),
    discount: money('100.00'),
    adjustment: 'SiblingDiscount',
    discountPercent: 10,
    ...overrides,
  } satisfies components['schemas']['OutstandingDueDto'];
}

export function group(overrides: Partial<components['schemas']['ChildGroupDto']> = {}) {
  return {
    enrollmentId: ENROLLMENT_ID,
    groupId: GROUP_ID,
    groupName: 'U10 Yüzme',
    sportName: 'Yüzme',
    facilityName: 'Kapalı Havuz',
    coachFirstName: 'Can',
    coachLastName: 'Koç',
    startDate: '2026-09-01',
    slots: [{ day: 'Wednesday', start: '17:00', end: '18:00' }],
    ...overrides,
  } satisfies components['schemas']['ChildGroupDto'];
}

export function attendance(
  sessions: components['schemas']['StudentSessionDto'][] = [],
): components['schemas']['StudentAttendanceDto'] {
  return {
    studentId: CHILD_ID,
    firstName: 'Ada',
    lastName: 'Deneme',
    from: '2026-10-13',
    to: '2026-11-11',
    sessions,
    summary: {
      sessions: sessions.length,
      present: 1,
      late: 0,
      absent: 0,
      excused: 0,
      notRecorded: 0,
      rate: 1,
    },
  };
}

export const emptyPage = { items: [], page: 1, pageSize: 10, totalCount: 0, totalPages: 0 };

/** The read endpoints of one child, answering with the given data. */
export function childRoutes(data: {
  children?: unknown[];
  schedule?: unknown[];
  attendance?: unknown;
  dues?: unknown[];
  payments?: unknown;
  announcements?: unknown;
}): Record<string, Handler> {
  const base = `/api/v1/parent/children/${CHILD_ID}`;
  return {
    'GET /api/v1/parent/children': () => json(data.children ?? [child()]),
    [`GET ${base}/schedule`]: () => json(data.schedule ?? [group()]),
    [`GET ${base}/attendance`]: () => json(data.attendance ?? attendance()),
    [`GET ${base}/dues`]: () => json(data.dues ?? []),
    [`GET ${base}/payments`]: () => json(data.payments ?? emptyPage),
    [`GET ${base}/announcements`]: () => json(data.announcements ?? emptyPage),
  };
}
