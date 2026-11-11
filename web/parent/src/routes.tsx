import type { RouteObject } from 'react-router';
import { GuestOnly, RequireSession } from './auth/guards';
import { AppShell } from './layout/AppShell';
import { ForgotPasswordPage } from './pages/auth/ForgotPasswordPage';
import { LoginPage } from './pages/auth/LoginPage';
import { ResetPasswordPage } from './pages/auth/ResetPasswordPage';
import { ChildPage } from './pages/ChildPage';
import { ChildrenPage } from './pages/ChildrenPage';
import { ErrorPage } from './pages/ErrorPage';
import { NotFoundPage } from './pages/NotFoundPage';
import { PaymentResultPage } from './pages/payments/PaymentResultPage';
import { SandboxCheckoutPage } from './pages/payments/SandboxCheckoutPage';

/**
 * Route table, shared by the browser router (main.tsx) and the memory router in tests. Public pages: login and
 * forgot password (signed-out only), the invitation/reset link page, and the payment pages a provider returns to
 * (`/sandbox-odeme`, `/odeme-sonucu`: paths fixed by the API, DEC-347/DEC-348). Everything else needs a session.
 */
export const routes: RouteObject[] = [
  {
    errorElement: <ErrorPage />,
    children: [
      {
        element: <GuestOnly />,
        children: [
          { path: 'login', element: <LoginPage /> },
          { path: 'forgot-password', element: <ForgotPasswordPage /> },
        ],
      },
      { path: 'reset-password', element: <ResetPasswordPage /> },
      { path: 'sandbox-odeme', element: <SandboxCheckoutPage /> },
      { path: 'odeme-sonucu', element: <PaymentResultPage /> },
      {
        element: <RequireSession />,
        children: [
          {
            element: <AppShell />,
            children: [
              { index: true, element: <ChildrenPage /> },
              { path: 'cocuk/:id', element: <ChildPage /> },
              { path: '*', element: <NotFoundPage /> },
            ],
          },
        ],
      },
    ],
  },
];
