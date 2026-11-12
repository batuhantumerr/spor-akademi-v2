import { act, render, screen } from '@testing-library/react';
import { Outlet, RouterProvider, createMemoryRouter } from 'react-router';
import { describe, expect, it } from 'vitest';
import { useDocumentTitle } from '../pages/use-document-title';
import { RouteAnnouncer } from './RouteAnnouncer';

function Page({ title }: { title: string }) {
  useDocumentTitle(title);
  return (
    <main>
      <h1>{title}</h1>
      <input aria-label="filter" />
    </main>
  );
}

function renderRoutes() {
  const router = createMemoryRouter(
    [
      {
        element: (
          <>
            <Outlet />
            <RouteAnnouncer />
          </>
        ),
        children: [
          { path: '/', element: <Page title="Çocuklarım" /> },
          { path: '/cocuk/1', element: <Page title="Ece" /> },
        ],
      },
    ],
    { initialEntries: ['/'] },
  );
  render(<RouterProvider router={router} />);
  return router;
}

describe('RouteAnnouncer (P3-11, DEC-438)', () => {
  it('leaves the first render to the browser', () => {
    renderRoutes();
    expect(screen.getByTestId('route-announcer')).toHaveTextContent('');
    expect(document.activeElement).toBe(document.body);
  });

  it('moves focus to main and announces the title after a navigation to another path', async () => {
    const router = renderRoutes();
    await act(() => router.navigate('/cocuk/1'));

    expect(screen.getByTestId('route-announcer')).toHaveTextContent('Ece ·');
    expect(document.activeElement).toBe(screen.getByRole('main'));
    expect(screen.getByRole('main')).toHaveAttribute('tabindex', '-1');
  });

  it('keeps focus when only the query string changes', async () => {
    const router = renderRoutes();
    const filter = screen.getByLabelText('filter');
    filter.focus();
    await act(() => router.navigate('/?sayfa=2'));

    expect(document.activeElement).toBe(filter);
    expect(screen.getByTestId('route-announcer')).toHaveTextContent('');
  });
});
