import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { ActivatedRoute } from '@angular/router';
import { LucideCircleCheck, LucideCircleX, LucideClock } from '@lucide/angular';

import { ui } from '../../core/i18n/tr';

const TONES: Record<string, string> = {
  Succeeded: 'bg-success/15 text-success',
  Pending: 'bg-primary/10 text-primary',
};

const KNOWN = ['Succeeded', 'Failed', 'Pending', 'Expired'];

/** The status shown for the `durum` query parameter; anything unexpected reads as "unknown". */
export function resultStatus(value: string | null): string {
  if (value === 'Created') {
    return 'Pending';
  }
  return value && KNOWN.includes(value) ? value : 'Unknown';
}

/**
 * DEC-348: where the payer lands after the hosted checkout (public, no sign-in). It only shows the status the API put
 * in the redirect; the school sees the authoritative result in the admin panel.
 */
@Component({
  selector: 'app-payment-result-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [LucideCircleCheck, LucideCircleX, LucideClock],
  template: `
    <main class="flex min-h-dvh items-center justify-center p-4">
      <section class="card w-full max-w-md p-6 text-center" aria-labelledby="result-title">
        <div class="mx-auto mb-4 grid size-12 place-items-center rounded-2xl" [class]="tone">
          @switch (status) {
            @case ('Succeeded') {
              <svg lucideCircleCheck class="size-6"></svg>
            }
            @case ('Pending') {
              <svg lucideClock class="size-6"></svg>
            }
            @default {
              <svg lucideCircleX class="size-6"></svg>
            }
          }
        </div>
        <h1 id="result-title" class="text-xl font-bold">{{ text['title'] }}</h1>
        <p class="mt-2 text-sm text-muted-foreground" data-testid="result-message" role="status">
          {{ text[status] }}
        </p>
      </section>
    </main>
  `,
})
export class PaymentResultPage {
  protected readonly text = ui.onlinePayments.result;
  protected readonly status = resultStatus(
    inject(ActivatedRoute).snapshot.queryParamMap.get('durum'),
  );
  protected readonly tone = TONES[this.status] ?? 'bg-destructive/10 text-destructive';
}
