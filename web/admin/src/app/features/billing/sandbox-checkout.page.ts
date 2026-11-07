import { DOCUMENT } from '@angular/common';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { ActivatedRoute } from '@angular/router';
import { LucideCreditCard } from '@lucide/angular';

import { Api } from '../../api/api';
import { apiV1SandboxCheckoutCompletePost } from '../../api/functions';
import { problemMessage, toProblem } from '../../core/api/problem';
import { ui } from '../../core/i18n/tr';

/**
 * Sends the browser to the provider callback like a hosted checkout does: a form POST with the token, so the API
 * answers with a redirect to the result page.
 */
export function postToCallback(document: Document, callbackUrl: string, token: string): void {
  const form = document.createElement('form');
  form.method = 'post';
  form.action = callbackUrl;
  const input = document.createElement('input');
  input.type = 'hidden';
  input.name = 'token';
  input.value = token;
  form.appendChild(input);
  document.body.appendChild(form);
  form.submit();
}

/**
 * DEC-347: the Fake provider's hosted checkout page (public, no sign-in). It stands in for iyzico's payment page in
 * development and demos: no card data, no money; the payer approves or declines.
 */
@Component({
  selector: 'app-sandbox-checkout-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [LucideCreditCard],
  template: `
    <main class="flex min-h-dvh items-center justify-center p-4">
      <section class="card w-full max-w-md p-6" aria-labelledby="sandbox-title">
        <div class="mb-4 grid size-12 place-items-center rounded-2xl bg-primary/10 text-primary">
          <svg lucideCreditCard class="size-6"></svg>
        </div>
        <h1 id="sandbox-title" class="text-xl font-bold">{{ text.title }}</h1>
        <p class="mt-2 text-sm text-muted-foreground">{{ text.description }}</p>
        @if (!token) {
          <p role="alert" class="mt-4 text-sm text-destructive">{{ text.missingToken }}</p>
        } @else {
          @if (error(); as message) {
            <p role="alert" class="mt-4 text-sm text-destructive">{{ message }}</p>
          }
          <div class="mt-6 flex flex-wrap gap-3">
            <button
              type="button"
              class="btn btn-primary"
              [disabled]="busy()"
              (click)="complete(true)"
            >
              {{ busy() ? text.working : text.pay }}
            </button>
            <button
              type="button"
              class="btn btn-secondary"
              [disabled]="busy()"
              (click)="complete(false)"
            >
              {{ text.decline }}
            </button>
          </div>
        }
      </section>
    </main>
  `,
})
export class SandboxCheckoutPage {
  private readonly api = inject(Api);
  private readonly document = inject(DOCUMENT);

  protected readonly text = ui.onlinePayments.sandbox;
  protected readonly token = inject(ActivatedRoute).snapshot.queryParamMap.get('token') ?? '';
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);

  protected async complete(succeed: boolean): Promise<void> {
    if (this.busy()) {
      return;
    }
    this.busy.set(true);
    this.error.set(null);
    try {
      const result = await this.api.invoke(apiV1SandboxCheckoutCompletePost, {
        body: { token: this.token, succeed },
      });
      postToCallback(this.document, result.callbackUrl, this.token);
    } catch (err) {
      this.error.set(problemMessage(toProblem(err)));
      this.busy.set(false);
    }
  }
}
