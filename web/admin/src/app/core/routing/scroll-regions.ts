import { DOCUMENT } from '@angular/common';
import { DestroyRef, Injectable, inject } from '@angular/core';

/** Horizontally scrolling boxes: `.table-shell` (styles.css) and plain `overflow-x-auto` wrappers (reports, charts). */
const SELECTOR = '.table-shell, .overflow-x-auto';
const FOCUSABLE =
  'a[href], button:not([disabled]), input, select, textarea, [tabindex]:not([tabindex="-1"])';

/**
 * Keyboard access to tables that scroll sideways on a narrow screen (P3-11, DEC-442; WCAG 2.1.1, axe
 * `scrollable-region-focusable`). A table box that is wider than its content area and has nothing focusable inside
 * becomes a Tab stop (arrow keys then scroll it), named after its table's caption or label; when it fits again, the
 * stop is removed so wide screens keep a short Tab order. One observer for the whole app instead of a directive in
 * every page.
 */
@Injectable({ providedIn: 'root' })
export class ScrollRegions {
  private readonly document = inject(DOCUMENT);
  private readonly watched = new WeakSet<HTMLElement>();
  private resize: ResizeObserver | null = null;

  /** Starts watching; call once at start-up. */
  watch(): void {
    const view = this.document.defaultView;
    if (
      !view ||
      typeof view.ResizeObserver !== 'function' ||
      typeof view.MutationObserver !== 'function'
    ) {
      return;
    }
    this.resize = new view.ResizeObserver((entries) => {
      for (const entry of entries) this.update(entry.target as HTMLElement);
    });
    let pending = false;
    const scan = () => {
      pending = false;
      for (const box of Array.from(this.document.querySelectorAll<HTMLElement>(SELECTOR))) {
        if (!this.watched.has(box)) {
          this.watched.add(box);
          this.resize?.observe(box);
        }
        this.update(box);
      }
    };
    const mutations = new view.MutationObserver(() => {
      if (!pending) {
        pending = true;
        view.requestAnimationFrame(scan);
      }
    });
    mutations.observe(this.document.body, { childList: true, subtree: true });
    inject(DestroyRef).onDestroy(() => {
      mutations.disconnect();
      this.resize?.disconnect();
    });
  }

  private update(box: HTMLElement): void {
    // Boxes a page made a Tab stop itself (tabindex in the template) are left as they are.
    const ours = box.hasAttribute('data-scroll-stop');
    if (!ours && box.hasAttribute('tabindex')) return;
    const scrolls = box.scrollWidth > box.clientWidth + 1;
    const reachable = box.querySelector(FOCUSABLE) !== null;
    if (scrolls && !reachable) {
      if (ours) return;
      box.setAttribute('data-scroll-stop', '');
      box.setAttribute('tabindex', '0');
      const table = box.querySelector('table');
      const name =
        table?.getAttribute('aria-label') ??
        table?.querySelector('caption')?.textContent?.trim() ??
        '';
      if (name) {
        box.setAttribute('role', 'region');
        box.setAttribute('aria-label', name);
      }
    } else if (ours) {
      for (const attribute of ['data-scroll-stop', 'tabindex', 'role', 'aria-label']) {
        box.removeAttribute(attribute);
      }
    }
  }
}
