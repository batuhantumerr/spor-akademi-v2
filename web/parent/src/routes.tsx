import type { RouteObject } from 'react-router';
import { AppShell } from './layout/AppShell';
import { ChildrenPage } from './pages/ChildrenPage';
import { ErrorPage } from './pages/ErrorPage';
import { NotFoundPage } from './pages/NotFoundPage';

/** Route table, shared by the browser router (main.tsx) and the memory router in tests. */
export const routes: RouteObject[] = [
  {
    element: <AppShell />,
    errorElement: <ErrorPage />,
    children: [
      { index: true, element: <ChildrenPage /> },
      { path: '*', element: <NotFoundPage /> },
    ],
  },
];
