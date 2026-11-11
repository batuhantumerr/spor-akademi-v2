import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { session } from './auth/session';
import { text } from './i18n/tr';
import { childRoutes, fakeApi, json, parentUser, problem, sessionResponse, signIn } from './test/fake-api';
import { renderApp } from './test/render-app';

describe('routes and session guards', () => {
  it('sends a signed-out visitor to the login page and back after signing in', async () => {
    fakeApi({
      ...childRoutes({}),
      'POST /api/v1/auth/login': () => json(sessionResponse()),
    });
    const { router } = renderApp('/cocuk/abc?x=1');

    expect(await screen.findByRole('heading', { level: 1, name: text.login.title })).toBeInTheDocument();
    expect(router.state.location.search).toBe(`?returnUrl=${encodeURIComponent('/cocuk/abc?x=1')}`);

    await userEvent.type(screen.getByLabelText(text.login.email), 'veli@example.test');
    await userEvent.type(screen.getByLabelText(text.login.password), 'uzun-bir-sifre-123');
    await userEvent.click(screen.getByRole('button', { name: text.login.submit }));

    await waitFor(() => {
      expect(router.state.location.pathname).toBe('/cocuk/abc');
    });
  });

  it('never follows a returnUrl to another site', async () => {
    fakeApi({ ...childRoutes({}), 'POST /api/v1/auth/login': () => json(sessionResponse()) });
    const { router } = renderApp('/login?returnUrl=%2F%2Fevil.example');

    await userEvent.type(await screen.findByLabelText(text.login.email), 'veli@example.test');
    await userEvent.type(screen.getByLabelText(text.login.password), 'uzun-bir-sifre-123');
    await userEvent.click(screen.getByRole('button', { name: text.login.submit }));

    await waitFor(() => {
      expect(router.state.location.pathname).toBe('/');
    });
  });

  it('shows why a login was refused and clears the password', async () => {
    fakeApi({
      'POST /api/v1/auth/login': () => json(sessionResponse(parentUser({ role: 'SchoolAdmin' }))),
      'POST /api/v1/auth/logout': () => new Response(null, { status: 204 }),
    });
    renderApp('/login');

    await userEvent.type(await screen.findByLabelText(text.login.email), 'mudur@example.test');
    await userEvent.type(screen.getByLabelText(text.login.password), 'uzun-bir-sifre-123');
    await userEvent.click(screen.getByRole('button', { name: text.login.submit }));

    expect(await screen.findByRole('alert')).toHaveTextContent('Bu uygulama velilere özeldir.');
    expect(screen.getByLabelText(text.login.password)).toHaveValue('');
    expect(session.getState().status).toBe('signedOut');
  });

  it('asks for both fields before calling the API', async () => {
    const api = fakeApi({});
    renderApp('/login');

    await userEvent.click(await screen.findByRole('button', { name: text.login.submit }));

    expect(screen.getAllByText('Bu alan zorunludur.')).toHaveLength(2);
    expect(screen.getByLabelText(text.login.email)).toHaveFocus();
    expect(api.calls).toHaveLength(0);
  });

  it('tells a parent whose session expired why they are on the login page', async () => {
    renderApp('/login?expired=1');

    expect(await screen.findByText(text.login.expired)).toBeInTheDocument();
  });

  it('keeps a signed-in parent away from the login page', async () => {
    signIn();
    fakeApi(childRoutes({}));
    const { router } = renderApp('/login');

    expect(await screen.findByRole('heading', { level: 1, name: text.children.title })).toBeInTheDocument();
    expect(router.state.location.pathname).toBe('/');
  });

  it('waits for the start-up refresh before deciding', async () => {
    session.forget();
    // A page load starts in "loading" until the refresh answers.
    Object.assign(session, { state: { status: 'loading', user: null, expired: false } });
    renderApp('/');

    expect(screen.getByRole('status')).toHaveTextContent(text.app.loading);
    session.adopt('access-1', parentUser());
    fakeApi(childRoutes({}));
    expect(await screen.findByRole('heading', { level: 1, name: text.children.title })).toBeInTheDocument();
  });

  it('shows the 404 page for an unknown path and links back home', async () => {
    signIn();
    fakeApi(childRoutes({}));
    renderApp('/no/such/page');

    expect(await screen.findByRole('heading', { level: 1, name: text.notFound.title })).toBeInTheDocument();
    await userEvent.click(screen.getByRole('link', { name: text.notFound.backHome }));
    expect(await screen.findByRole('heading', { level: 1, name: text.children.title })).toBeInTheDocument();
  });

  it('marks the home link as the current page and offers a skip link', async () => {
    signIn();
    fakeApi(childRoutes({}));
    renderApp('/');

    const navs = await screen.findAllByRole('navigation', { name: text.app.mainNavigation });
    for (const nav of navs) {
      expect(within(nav).getByRole('link', { name: text.nav.children })).toHaveAttribute(
        'aria-current',
        'page',
      );
    }
    expect(screen.getByRole('link', { name: text.app.skipToContent })).toHaveAttribute('href', '#main');
  });

  it('signs out: the session ends here and on the server, and the login page shows', async () => {
    signIn();
    const api = fakeApi({
      ...childRoutes({}),
      'POST /api/v1/auth/logout': () => new Response(null, { status: 204 }),
    });
    renderApp('/');

    await userEvent.click(await screen.findByRole('button', { name: text.nav.logout }));

    expect(await screen.findByRole('heading', { level: 1, name: text.login.title })).toBeInTheDocument();
    expect(session.token()).toBeNull();
    expect(api.callsTo('POST /api/v1/auth/logout')).toHaveLength(1);
  });

  it('sends an expired session to the login page with the reason', async () => {
    signIn();
    fakeApi({
      ...childRoutes({}),
      'GET /api/v1/parent/children': () => problem(401, 'Auth.Unauthenticated'),
      'POST /api/v1/auth/refresh': () => problem(401, 'Auth.SessionExpired'),
    });
    const { router } = renderApp('/');

    expect(await screen.findByText(text.login.expired)).toBeInTheDocument();
    expect(router.state.location.search).toBe('?expired=1');
  });
});
