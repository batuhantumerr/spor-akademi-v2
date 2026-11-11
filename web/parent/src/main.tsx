import '@fontsource-variable/inter';
import './styles.css';

import { QueryClientProvider } from '@tanstack/react-query';
import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { createBrowserRouter, RouterProvider } from 'react-router';
import { createQueryClient } from './query/query-client';
import { session } from './auth/session';
import { routes } from './routes';
import { applyTheme } from './theme/theme';

applyTheme();

const router = createBrowserRouter(routes);
const queryClient = createQueryClient();
// Personal data never outlives the session: logout and expiry drop every cached answer.
session.onEnd(() => {
  queryClient.clear();
});
// One refresh call restores a session after a reload (ADR-0006); the guards wait for it.
void session.restore();
const root = document.getElementById('root');
if (!root) throw new Error('Missing #root element.');

createRoot(root).render(
  <StrictMode>
    <QueryClientProvider client={queryClient}>
      <RouterProvider router={router} />
    </QueryClientProvider>
  </StrictMode>,
);
