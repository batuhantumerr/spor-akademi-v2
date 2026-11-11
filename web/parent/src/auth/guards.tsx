import { Navigate, Outlet, useLocation } from 'react-router';
import { text } from '../i18n/tr';
import { safeReturnUrl } from './links';
import { useSession } from './session';

/** Full-page placeholder while the start-up refresh decides whether there is a session. */
export function BootLoading() {
  return (
    <div role="status" className="flex min-h-dvh items-center justify-center gap-3 text-sm">
      <span
        aria-hidden="true"
        className="size-5 animate-spin rounded-full border-2 border-current border-r-transparent motion-reduce:animate-none"
      />
      {text.app.loading}
    </div>
  );
}

/** Signed-in parents only; others go to the login page and come back afterwards. UI only, the API enforces too. */
export function RequireSession() {
  const state = useSession();
  const location = useLocation();
  if (state.status === 'loading') return <BootLoading />;
  if (state.status === 'signedOut') {
    const params = new URLSearchParams();
    const here = location.pathname + location.search;
    if (here !== '/') params.set('returnUrl', here);
    if (state.expired) params.set('expired', '1');
    const query = params.toString();
    return <Navigate to={query ? `/login?${query}` : '/login'} replace />;
  }
  return <Outlet />;
}

/** The login page is only for signed-out visitors. */
export function GuestOnly() {
  const state = useSession();
  const location = useLocation();
  if (state.status === 'loading') return <BootLoading />;
  if (state.status === 'signedIn') {
    return <Navigate to={safeReturnUrl(new URLSearchParams(location.search).get('returnUrl'))} replace />;
  }
  return <Outlet />;
}
