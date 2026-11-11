import { describe, expect, it } from 'vitest';
import { session } from '../auth/session';
import {
  CHILD_ID,
  child,
  fakeApi,
  json,
  parentUser,
  problem,
  sessionResponse,
  signIn,
} from '../test/fake-api';
import { api } from './client';
import { unwrap } from './errors';

const CHILDREN = 'GET /api/v1/parent/children';

describe('api client', () => {
  it('sends the in-memory token to API calls on the page origin', async () => {
    signIn(parentUser(), 'access-1');
    const fake = fakeApi({ [CHILDREN]: () => json([child()]) });

    const children = await unwrap(api.GET('/api/v1/parent/children'));

    expect(children[0]?.id).toBe(CHILD_ID);
    const [request] = fake.calls;
    expect(new URL(request!.url).origin).toBe(window.location.origin);
    expect(request!.headers.get('Authorization')).toBe('Bearer access-1');
  });

  it('never sends the token to the auth endpoints', async () => {
    signIn(parentUser(), 'access-1');
    const fake = fakeApi({
      'POST /api/v1/auth/password-reset/request': () => new Response(null, { status: 202 }),
    });

    await unwrap(api.POST('/api/v1/auth/password-reset/request', { body: { email: 'veli@example.test' } }));

    expect(fake.calls[0]!.headers.get('Authorization')).toBeNull();
  });

  it('refreshes once on an expired token and retries with the new one', async () => {
    signIn(parentUser(), 'old');
    const fake = fakeApi({
      [CHILDREN]: (request) =>
        request.headers.get('Authorization') === 'Bearer new'
          ? json([child()])
          : problem(401, 'Auth.Unauthenticated'),
      'POST /api/v1/auth/refresh': () => json(sessionResponse(parentUser(), 'new')),
    });

    const children = await unwrap(api.GET('/api/v1/parent/children'));

    expect(children).toHaveLength(1);
    expect(fake.callsTo('POST /api/v1/auth/refresh')).toHaveLength(1);
    expect(fake.callsTo(CHILDREN).map((r) => r.headers.get('Authorization'))).toEqual([
      'Bearer old',
      'Bearer new',
    ]);
  });

  it('expires the session when the refresh cookie is rejected', async () => {
    signIn(parentUser(), 'old');
    fakeApi({
      [CHILDREN]: () => problem(401, 'Auth.Unauthenticated'),
      'POST /api/v1/auth/refresh': () => problem(401, 'Auth.SessionExpired'),
    });

    await expect(unwrap(api.GET('/api/v1/parent/children'))).rejects.toMatchObject({ status: 401 });

    expect(session.getState()).toMatchObject({ status: 'signedOut', expired: true });
  });

  it('does not refresh on a coded business 401 or a 403', async () => {
    signIn();
    const fake = fakeApi({ [CHILDREN]: () => problem(403, 'Auth.Forbidden') });

    await expect(unwrap(api.GET('/api/v1/parent/children'))).rejects.toMatchObject({
      code: 'Auth.Forbidden',
    });

    expect(fake.callsTo('POST /api/v1/auth/refresh')).toHaveLength(0);
    expect(session.getState().status).toBe('signedIn');
  });

  it('reports a lost connection as Network.Unavailable', async () => {
    signIn();
    fakeApi({ [CHILDREN]: () => Promise.reject(new TypeError('offline')) });

    await expect(unwrap(api.GET('/api/v1/parent/children'))).rejects.toMatchObject({
      code: 'Network.Unavailable',
    });
  });
});
