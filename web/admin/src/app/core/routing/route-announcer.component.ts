import { DOCUMENT } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  Injector,
  afterNextRender,
  inject,
  signal,
} from '@angular/core';
import { NavigationEnd, Router } from '@angular/router';
import { filter } from 'rxjs';

/**
 * Route changes for screen reader and keyboard users (P3-11, DEC-438; WCAG 2.4.3, 4.1.3). A single-page app
 * swaps the page without a load, so after each navigation to another path:
 * - the new page's title (`<title>`, set by AppTitleStrategy) is announced through a polite live region;
 * - focus moves to the page's `<main>` (or its `<h1>`), so the next Tab starts in the new content, not in the menu
 *   link or button that was used.
 * The first navigation (page load) and query-only changes (list filters kept in the URL) are left alone: the browser
 * already announces a load, and a filter must keep focus on its own control.
 */
@Component({
  selector: 'app-route-announcer',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<div
    class="sr-only"
    aria-live="polite"
    aria-atomic="true"
    data-testid="route-announcer"
  >
    {{ message() }}
  </div>`,
})
export class RouteAnnouncerComponent {
  private readonly document = inject(DOCUMENT);
  private readonly injector = inject(Injector);
  protected readonly message = signal('');
  private lastPath: string | null = null;

  constructor() {
    const subscription = inject(Router)
      .events.pipe(filter((e): e is NavigationEnd => e instanceof NavigationEnd))
      .subscribe((event) => {
        const path = event.urlAfterRedirects.split(/[?#]/)[0];
        const previous = this.lastPath;
        this.lastPath = path;
        if (previous === null || previous === path) return;
        // After the new page has rendered (its <main>/<h1> exist and the title strategy has run).
        afterNextRender(() => this.announce(), { injector: this.injector });
      });
    inject(DestroyRef).onDestroy(() => subscription.unsubscribe());
  }

  private announce(): void {
    this.message.set(this.document.title);
    // A dialog that is still open (e.g. one that navigated on save) keeps focus; it restores focus itself.
    if (this.document.activeElement?.closest('[role="dialog"], [role="alertdialog"]')) return;
    const target =
      this.document.querySelector<HTMLElement>('main') ??
      this.document.querySelector<HTMLElement>('h1');
    if (!target) return;
    if (!target.hasAttribute('tabindex')) target.setAttribute('tabindex', '-1');
    target.focus({ preventScroll: true });
  }
}
