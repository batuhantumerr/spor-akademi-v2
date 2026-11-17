import { Dialog } from '@angular/cdk/dialog';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';

import { problemMessage, toProblem } from '../../core/api/problem';
import { MfaService, MfaStatus } from '../../core/auth/mfa.service';
import { interpolate, ui } from '../../core/i18n/tr';
import { PageHeaderComponent } from '../../shared/ui/page-header.component';
import { ToastService } from '../../shared/ui/toast.service';
import { MfaSetupComponent } from '../auth/mfa-setup.component';
import { DISABLE_MFA_TITLE_ID, DisableMfaDialog } from './disable-mfa.dialog';

/**
 * P5-01: the signed-in admin's two-step verification (TOTP). A School Admin turns it on and off; for a Platform Admin it
 * is mandatory, so there is no way to turn it off. Turning it off asks for a current code.
 */
@Component({
  selector: 'app-security-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [PageHeaderComponent, MfaSetupComponent],
  template: `
    <app-page-header [title]="text.title" [description]="text.description" />

    @if (loadError(); as message) {
      <div
        role="alert"
        class="card flex items-center justify-between gap-3 p-4 text-sm text-destructive"
      >
        <span>{{ message }}</span>
        <button type="button" class="btn btn-secondary btn-sm" (click)="load()">
          {{ common.retry }}
        </button>
      </div>
    } @else if (!status()) {
      <p role="status" class="card p-6 text-sm text-muted-foreground">{{ common.loading }}</p>
    } @else {
      @let s = status()!;
      <section class="card max-w-2xl p-5" aria-labelledby="mfa-section">
        <div class="flex flex-wrap items-center justify-between gap-3">
          <h2 id="mfa-section" class="text-base font-semibold">{{ text.section }}</h2>
          <span class="badge" [class.badge-success]="s.enabled" [class.badge-muted]="!s.enabled">
            {{ s.enabled ? text.on : text.off }}
          </span>
        </div>

        @if (!s.available) {
          <p class="mt-3 text-sm text-muted-foreground">{{ text.unavailable }}</p>
        } @else if (enrolling()) {
          <div class="mt-4">
            <app-mfa-setup [begin]="begin" [confirm]="confirm" (finished)="finishEnrolment()" />
          </div>
        } @else {
          <p class="mt-3 text-sm text-muted-foreground">
            @if (s.enabled) {
              {{ enabledText(s) }}
            } @else {
              {{ text.offText }}
            }
          </p>
          @if (s.enabled && s.required) {
            <p class="mt-2 text-sm text-muted-foreground">{{ text.requiredText }}</p>
          }
          <div class="mt-4">
            @if (!s.enabled) {
              <button type="button" class="btn btn-primary" (click)="enrolling.set(true)">
                {{ text.enable }}
              </button>
            } @else if (!s.required) {
              <button type="button" class="btn btn-secondary" (click)="disable()">
                {{ text.disable }}
              </button>
            }
          </div>
        }
      </section>
    }
  `,
})
export class SecurityPage {
  private readonly mfa = inject(MfaService);
  private readonly dialog = inject(Dialog);
  private readonly toasts = inject(ToastService);

  protected readonly text = ui.mfa.settings;
  protected readonly common = ui.common;
  protected readonly status = signal<MfaStatus | null>(null);
  protected readonly loadError = signal<string | null>(null);
  protected readonly enrolling = signal(false);

  protected readonly begin = () => this.mfa.startEnrollment();
  protected readonly confirm = (code: string) => this.mfa.confirmEnrollment(code);

  constructor() {
    this.load();
  }

  protected load(): void {
    this.loadError.set(null);
    this.mfa.status().subscribe({
      next: (status) => this.status.set(status),
      error: (err: unknown) => this.loadError.set(problemMessage(toProblem(err))),
    });
  }

  protected enabledText(status: MfaStatus): string {
    return interpolate(this.text.onText, { remaining: status.recoveryCodesRemaining });
  }

  protected finishEnrolment(): void {
    this.enrolling.set(false);
    this.toasts.success(this.text.enabled);
    this.load();
  }

  protected async disable(): Promise<void> {
    const ref = this.dialog.open<boolean>(DisableMfaDialog, {
      ariaLabelledBy: DISABLE_MFA_TITLE_ID,
      backdropClass: ['bg-slate-900/60'],
    });
    if ((await firstValueFrom(ref.closed)) === true) {
      this.toasts.success(this.text.disabled);
      this.load();
    }
  }
}
