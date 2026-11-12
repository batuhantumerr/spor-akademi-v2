import { QueryClientProvider } from '@tanstack/react-query';
import { render, type RenderResult } from '@testing-library/react';
import { createMemoryRouter } from 'react-router';
import { LocalizedRouter } from '../i18n/LocalizedRouter';
import { createQueryClient } from '../query/query-client';
import { routes } from '../routes';

export type AppRouter = ReturnType<typeof createMemoryRouter>;

/** Renders the real route table (behind the language switch, as main.tsx does) at `path` with a fresh query client; returns the router to read the location. */
export function renderApp(path = '/'): RenderResult & { router: AppRouter } {
  const router = createMemoryRouter(routes, { initialEntries: [path] });
  const client = createQueryClient();
  client.setDefaultOptions({ queries: { ...client.getDefaultOptions().queries, retry: false } });
  const result = render(
    <QueryClientProvider client={client}>
      <LocalizedRouter router={router} />
    </QueryClientProvider>,
  );
  return { ...result, router };
}
