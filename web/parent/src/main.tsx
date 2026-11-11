import '@fontsource-variable/inter';
import './styles.css';

import { QueryClientProvider } from '@tanstack/react-query';
import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { createBrowserRouter, RouterProvider } from 'react-router';
import { createQueryClient } from './query/query-client';
import { routes } from './routes';
import { applyTheme } from './theme/theme';

applyTheme();

const router = createBrowserRouter(routes);
const queryClient = createQueryClient();
const root = document.getElementById('root');
if (!root) throw new Error('Missing #root element.');

createRoot(root).render(
  <StrictMode>
    <QueryClientProvider client={queryClient}>
      <RouterProvider router={router} />
    </QueryClientProvider>
  </StrictMode>,
);
