import { QueryClient } from '@tanstack/react-query';
import { toApiError } from '../api/errors';

/**
 * Server state lives in TanStack Query. Parents mostly read; a short stale time avoids refetch storms on
 * tab switches while keeping lists fresh. Personal data stays in memory only (no persister, P3-07).
 */
export function createQueryClient(): QueryClient {
  return new QueryClient({
    defaultOptions: {
      queries: {
        staleTime: 30_000,
        // Only a lost connection or a server error is worth one more try; 4xx answers will not change.
        retry: (failures, error) => {
          const { status } = toApiError(error);
          return failures < 1 && (status === 0 || status >= 500);
        },
        refetchOnWindowFocus: true,
      },
      mutations: {
        retry: 0,
      },
    },
  });
}
