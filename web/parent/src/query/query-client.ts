import { QueryClient } from '@tanstack/react-query';

/**
 * Server state lives in TanStack Query. Parents mostly read; a short stale time avoids refetch storms on
 * tab switches while keeping lists fresh. Personal data stays in memory only (no persister, P3-07).
 */
export function createQueryClient(): QueryClient {
  return new QueryClient({
    defaultOptions: {
      queries: {
        staleTime: 30_000,
        retry: 1,
        refetchOnWindowFocus: true,
      },
      mutations: {
        retry: 0,
      },
    },
  });
}
