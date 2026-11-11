import { useInfiniteQuery, useQuery } from '@tanstack/react-query';
import { api, type components } from '../api/client';
import { unwrap } from '../api/errors';

export type Child = components['schemas']['ParentChildDto'];
export type ChildGroup = components['schemas']['ChildGroupDto'];
export type ChildAttendance = components['schemas']['StudentAttendanceDto'];
export type Due = components['schemas']['OutstandingDueDto'];
export type ParentPayment = components['schemas']['ParentPaymentDto'];
export type ParentAnnouncement = components['schemas']['ParentAnnouncementDto'];
export type ParentOnlinePayment = components['schemas']['ParentOnlinePaymentDto'];

/** Page size of the paged lists (payments, announcements). */
export const PAGE_SIZE = 10;

/**
 * Query keys under one root, so the whole parent cache can be dropped or refetched at once (logout, a returned
 * payment). The API is rate limited per account (DEC-388: 60 requests a minute), so lists keep the 30-second
 * stale time of the query client.
 */
export const parentKeys = {
  all: ['parent'] as const,
  children: () => [...parentKeys.all, 'children'] as const,
  child: (id: string) => [...parentKeys.all, 'child', id] as const,
  schedule: (id: string) => [...parentKeys.child(id), 'schedule'] as const,
  attendance: (id: string) => [...parentKeys.child(id), 'attendance'] as const,
  dues: (id: string) => [...parentKeys.child(id), 'dues'] as const,
  payments: (id: string) => [...parentKeys.child(id), 'payments'] as const,
  announcements: (id: string) => [...parentKeys.child(id), 'announcements'] as const,
};

const path = (id: string) => ({ params: { path: { id } } });

export function useChildren() {
  return useQuery({
    queryKey: parentKeys.children(),
    queryFn: ({ signal }) => unwrap(api.GET('/api/v1/parent/children', { signal })),
  });
}

export function useSchedule(id: string) {
  return useQuery({
    queryKey: parentKeys.schedule(id),
    queryFn: ({ signal }) =>
      unwrap(api.GET('/api/v1/parent/children/{id}/schedule', { ...path(id), signal })),
  });
}

/** The API's default window: the last 30 days including today, in the school's time zone. */
export function useAttendance(id: string) {
  return useQuery({
    queryKey: parentKeys.attendance(id),
    queryFn: ({ signal }) =>
      unwrap(api.GET('/api/v1/parent/children/{id}/attendance', { ...path(id), signal })),
  });
}

export function useDues(id: string) {
  return useQuery({
    queryKey: parentKeys.dues(id),
    queryFn: ({ signal }) => unwrap(api.GET('/api/v1/parent/children/{id}/dues', { ...path(id), signal })),
  });
}

export function usePayments(id: string) {
  return useInfiniteQuery({
    queryKey: parentKeys.payments(id),
    initialPageParam: 1,
    queryFn: ({ pageParam, signal }) =>
      unwrap(
        api.GET('/api/v1/parent/children/{id}/payments', {
          params: { path: { id }, query: { page: pageParam, pageSize: PAGE_SIZE } },
          signal,
        }),
      ),
    getNextPageParam: (last) =>
      Number(last.page) < Number(last.totalPages) ? Number(last.page) + 1 : undefined,
  });
}

export function useAnnouncements(id: string) {
  return useInfiniteQuery({
    queryKey: parentKeys.announcements(id),
    initialPageParam: 1,
    queryFn: ({ pageParam, signal }) =>
      unwrap(
        api.GET('/api/v1/parent/children/{id}/announcements', {
          params: { path: { id }, query: { page: pageParam, pageSize: PAGE_SIZE } },
          signal,
        }),
      ),
    getNextPageParam: (last) =>
      Number(last.page) < Number(last.totalPages) ? Number(last.page) + 1 : undefined,
  });
}

/**
 * Starts (or, with the same key, replays) an online payment for one due (DEC-386). The key belongs to one attempt
 * of one due; a retry after a network error must reuse it, so the API answers with the same attempt.
 */
export function startOnlinePayment(enrollmentId: string, period: string, idempotencyKey: string) {
  return unwrap(
    api.POST('/api/v1/parent/online-payments', {
      params: { header: { 'Idempotency-Key': idempotencyKey } },
      body: { enrollmentId, period },
    }),
  );
}
