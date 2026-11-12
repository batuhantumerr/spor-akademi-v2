import '@fontsource-variable/inter';
import './styles.css';

import { QueryClientProvider } from '@tanstack/react-query';
import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { createBrowserRouter } from 'react-router';
import { createQueryClient } from './query/query-client';
import { session } from './auth/session';
import { routes } from './routes';
import { startRealtime } from './realtime/realtime';
import { pwa, reloadOnceOnChunkError } from './pwa/pwa';
import { applyTheme } from './theme/theme';
import { applyLanguage } from './i18n/language';
import { LocalizedRouter } from './i18n/LocalizedRouter';

applyTheme();
applyLanguage();

const router = createBrowserRouter(routes);
const queryClient = createQueryClient();
// Personal data never outlives the session: logout and expiry drop every cached answer.
session.onEnd(() => {
  queryClient.clear();
});
// One refresh call restores a session after a reload (ADR-0006); the guards wait for it.
void session.restore();
// P3-08: the notifications hub follows the session (connects when signed in, disconnects on logout or expiry).
startRealtime(session, queryClient);
// P3-07: the service worker caches the app shell only (DEC-394); development builds run without it.
if (import.meta.env.PROD) {
  pwa.start();
  reloadOnceOnChunkError();
}
const root = document.getElementById('root');
if (!root) throw new Error('Missing #root element.');

createRoot(root).render(
  <StrictMode>
    <QueryClientProvider client={queryClient}>
      <LocalizedRouter router={router} />
    </QueryClientProvider>
  </StrictMode>,
);
