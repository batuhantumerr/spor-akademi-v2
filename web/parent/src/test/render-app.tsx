import { QueryClientProvider } from '@tanstack/react-query';
import { render, type RenderResult } from '@testing-library/react';
import { createMemoryRouter, RouterProvider } from 'react-router';
import { createQueryClient } from '../query/query-client';
import { routes } from '../routes';

export type AppRouter = ReturnType<typeof createMemoryRouter>;

/** Renders the real route table at `path` with a fresh query client; returns the router to read the location. */
export function renderApp(path = '/'): RenderResult & { router: AppRouter } {
  const router = createMemoryRouter(routes, { initialEntries: [path] });
  const client = createQueryClient();
  client.setDefaultOptions({ queries: { ...client.getDefaultOptions().queries, retry: false } });
  const result = render(
    <QueryClientProvider client={client}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  );
  return { ...result, router };
}
