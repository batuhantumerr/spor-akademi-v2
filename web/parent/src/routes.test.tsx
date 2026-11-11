import { screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { text } from './i18n/tr';
import { renderApp } from './test/render-app';

describe('routes', () => {
  it('shows the children placeholder on the home route', async () => {
    renderApp('/');

    expect(await screen.findByRole('heading', { level: 1, name: text.children.title })).toBeInTheDocument();
    expect(screen.getByText(text.children.placeholder)).toBeInTheDocument();
    expect(document.title).toBe(`${text.children.title} · ${text.app.name}`);
  });

  it('marks the home link as the current page in both navigations', async () => {
    renderApp('/');

    const navs = await screen.findAllByRole('navigation', { name: text.app.mainNavigation });
    expect(navs).toHaveLength(2);
    for (const nav of navs) {
      expect(within(nav).getByRole('link', { name: text.nav.children })).toHaveAttribute(
        'aria-current',
        'page',
      );
    }
  });

  it('shows the 404 page for an unknown path and links back home', async () => {
    renderApp('/no/such/page');

    expect(await screen.findByRole('heading', { level: 1, name: text.notFound.title })).toBeInTheDocument();

    await userEvent.click(screen.getByRole('link', { name: text.notFound.backHome }));

    expect(await screen.findByRole('heading', { level: 1, name: text.children.title })).toBeInTheDocument();
  });

  it('offers a skip link to the main content', async () => {
    renderApp('/');

    expect(await screen.findByRole('link', { name: text.app.skipToContent })).toHaveAttribute(
      'href',
      '#main',
    );
    expect(screen.getByRole('main')).toHaveAttribute('id', 'main');
  });
});
