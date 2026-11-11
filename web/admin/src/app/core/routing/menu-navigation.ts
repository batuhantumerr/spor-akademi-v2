import { Injectable, Injector, inject } from '@angular/core';
import { ActivatedRouteSnapshot, BaseRouteReuseStrategy, Router } from '@angular/router';

/**
 * Navigation state the side menu attaches to its links. Pages load their data when they are created, so a menu
 * click on the page that is already open must rebuild that page; otherwise its lists stay stale (#79).
 */
export const MENU_NAVIGATION_STATE: Readonly<Record<string, unknown>> = Object.freeze({
  menuNavigation: true,
});

/**
 * Reuses routes like Angular's default strategy, except that a menu navigation recreates the page component
 * (the deepest route). The shell and other parent routes stay in place, so the session and layout are kept.
 * Works together with `onSameUrlNavigation: 'reload'`, which lets a click on the current URL navigate at all.
 */
@Injectable({ providedIn: 'root' })
export class MenuNavigationReuseStrategy extends BaseRouteReuseStrategy {
  // The Router itself depends on the reuse strategy, so it is resolved lazily.
  private readonly injector = inject(Injector);

  override shouldReuseRoute(
    future: ActivatedRouteSnapshot,
    current: ActivatedRouteSnapshot,
  ): boolean {
    if (!super.shouldReuseRoute(future, current)) return false;
    const isPage = future.firstChild === null && future.component !== null;
    return !(isPage && this.isMenuNavigation());
  }

  private isMenuNavigation(): boolean {
    return this.injector.get(Router).currentNavigation()?.extras.state?.['menuNavigation'] === true;
  }
}
