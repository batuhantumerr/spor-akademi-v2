import { ChangeDetectionStrategy, Component, Injectable, inject, signal } from '@angular/core';
import { LucideCircleAlert, LucideCircleCheck, LucideX } from '@lucide/angular';

export interface Toast {
  id: number;
  kind: 'success' | 'error';
  message: string;
}

/** Short-lived notifications for completed or failed actions. */
@Injectable({ providedIn: 'root' })
export class ToastService {
  private nextId = 1;
  readonly toasts = signal<Toast[]>([]);

  success(message: string): void {
    this.push('success', message);
  }

  error(message: string): void {
    this.push('error', message);
  }

  dismiss(id: number): void {
    this.toasts.update((list) => list.filter((t) => t.id !== id));
  }

  private push(kind: Toast['kind'], message: string): void {
    const id = this.nextId++;
    this.toasts.update((list) => [...list, { id, kind, message }]);
    setTimeout(() => this.dismiss(id), kind === 'error' ? 7000 : 4000);
  }
}

@Component({
  selector: 'app-toasts',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [LucideCircleAlert, LucideCircleCheck, LucideX],
  template: `
    <div
      class="pointer-events-none fixed right-4 bottom-4 z-50 flex w-[min(24rem,calc(100vw-2rem))] flex-col gap-2"
      aria-live="polite"
    >
      @for (toast of toasts.toasts(); track toast.id) {
        <div
          role="status"
          class="pointer-events-auto flex items-start gap-3 rounded-xl border bg-card p-3 text-sm shadow-lg"
          [class.border-success/40]="toast.kind === 'success'"
          [class.border-destructive/40]="toast.kind === 'error'"
        >
          @if (toast.kind === 'success') {
            <svg lucideCircleCheck class="mt-0.5 size-4 shrink-0 text-success"></svg>
          } @else {
            <svg lucideCircleAlert class="mt-0.5 size-4 shrink-0 text-destructive"></svg>
          }
          <p class="flex-1 text-foreground">{{ toast.message }}</p>
          <button
            type="button"
            class="text-muted-foreground hover:text-foreground"
            aria-label="Kapat"
            (click)="toasts.dismiss(toast.id)"
          >
            <svg lucideX class="size-4"></svg>
          </button>
        </div>
      }
    </div>
  `,
})
export class ToastsComponent {
  protected readonly toasts = inject(ToastService);
}
