import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';

import { AuthService, Role } from './auth.service';

/** Signed-in users only; a pending password change takes precedence over everything else (US-05). */
export const authGuard: CanActivateFn = (_route, state) => {
  const auth = inject(AuthService);
  const router = inject(Router);
  if (!auth.isAuthenticated()) {
    return router.createUrlTree(['/login'], { queryParams: { returnUrl: state.url } });
  }
  if (auth.mustChangePassword()) {
    return router.createUrlTree(['/change-password']);
  }
  return true;
};

/** Restricts a route to roles; others land on their own home page. UI only — the API enforces too. */
export function roleGuard(...roles: Role[]): CanActivateFn {
  return () => {
    const auth = inject(AuthService);
    const role = auth.role();
    return role !== null && roles.includes(role)
      ? true
      : inject(Router).parseUrl(auth.homeFor(role));
  };
}

/** The login page is only for signed-out users. */
export const guestGuard: CanActivateFn = () => {
  const auth = inject(AuthService);
  return auth.isAuthenticated() ? inject(Router).parseUrl(auth.homeFor(auth.role())) : true;
};

/** The change-password page needs a session. */
export const sessionGuard: CanActivateFn = () => {
  const auth = inject(AuthService);
  return auth.isAuthenticated() ? true : inject(Router).parseUrl('/login');
};

/** Redirects the root path to the user's home. */
export const homeRedirectGuard: CanActivateFn = () => {
  const auth = inject(AuthService);
  return inject(Router).parseUrl(auth.homeFor(auth.role()));
};
