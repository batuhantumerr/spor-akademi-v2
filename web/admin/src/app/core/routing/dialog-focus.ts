import { Dialog } from '@angular/cdk/dialog';
import { DOCUMENT } from '@angular/common';
import { DestroyRef, Injectable, inject } from '@angular/core';

/** How long a page may keep the trigger disabled after its dialog closes (it usually reloads its list first). */
const RESTORE_WINDOW_MS = 3000;
const RETRY_MS = 50;

/**
 * Returns focus to the control that opened a dialog (P3-11, DEC-441; WCAG 2.4.3). CDK dialogs restore focus on
 * close, but the pages disable their buttons while a dialog and the work after it run (the re-entry guard), and a
 * disabled button cannot take focus: focus fell back to the page start. When a dialog closes and focus is on
 * nothing, this waits until the trigger is enabled again (or gone) and focuses it.
 */
@Injectable({ providedIn: 'root' })
export class DialogFocusRestorer {
  private readonly document = inject(DOCUMENT);

  /** Starts watching; call once at start-up. */
  watch(): void {
    const dialog = inject(Dialog);
    const subscription = dialog.afterOpened.subscribe((ref) => {
      const trigger = this.focusedOutsideDialogs();
      ref.closed.subscribe(() => {
        if (trigger) this.restore(trigger, Date.now());
      });
    });
    inject(DestroyRef).onDestroy(() => subscription.unsubscribe());
  }

  private focusedOutsideDialogs(): HTMLElement | null {
    const active = this.document.activeElement as HTMLElement | null;
    if (!active || active === this.document.body) return null;
    return active.closest('[role="dialog"], [role="alertdialog"]') ? null : active;
  }

  private restore(trigger: HTMLElement, started: number): void {
    const view = this.document.defaultView;
    if (!view) return;
    view.setTimeout(() => {
      const active = this.document.activeElement;
      // Somebody already placed focus (CDK itself, a route change, the user): leave it.
      if (active && active !== this.document.body) return;
      if (!trigger.isConnected) return;
      if (trigger.matches(':disabled')) {
        if (Date.now() - started < RESTORE_WINDOW_MS) this.restore(trigger, started);
        return;
      }
      trigger.focus();
    }, RETRY_MS);
  }
}
