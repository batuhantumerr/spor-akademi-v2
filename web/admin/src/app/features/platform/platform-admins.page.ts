import { Dialog } from '@angular/cdk/dialog';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';

import { problemMessage, toProblem } from '../../core/api/problem';
import { AuthService } from '../../core/auth/auth.service';
import { MfaService, PlatformAdminSummary } from '../../core/auth/mfa.service';
import { interpolate, ui } from '../../core/i18n/tr';
import { PageHeaderComponent } from '../../shared/ui/page-header.component';
import { ToastService } from '../../shared/ui/toast.service';
import { RESET_MFA_TITLE_ID, ResetMfaData, ResetMfaDialog } from './reset-mfa.dialog';

/**
 * V1-03 (DEC-688): the Platform Admins, and the way to reset another one's two-step verification after a lost phone
 * (the API of P5-01, DEC-605). Your own account has no button: resetting yourself is refused (`Mfa.CannotResetOwn`). The
 * reset is audited at platform level (DEC-689) and the person is told by e-mail.
 */
@Component({
  selector: 'app-platform-admins-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [PageHeaderComponent],
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
    } @else if (!admins()) {
      <p role="status" class="card p-6 text-sm text-muted-foreground">{{ common.loading }}</p>
    } @else if (admins()!.length === 0) {
      <p class="card p-6 text-sm text-muted-foreground">{{ text.empty }}</p>
    } @else {
      <div class="table-shell" tabindex="0" role="region" aria-labelledby="platform-admins-caption">
        <table class="data-table min-w-[40rem]">
          <caption id="platform-admins-caption" class="sr-only">
            {{
              text.caption
            }}
          </caption>
          <thead>
            <tr>
              <th scope="col">{{ text.name }}</th>
              <th scope="col">{{ text.status }}</th>
              <th scope="col">{{ text.mfa }}</th>
              <th scope="col">
                <span class="sr-only">{{ mfa.action }}</span>
              </th>
            </tr>
          </thead>
          <tbody>
            @for (admin of admins(); track admin.id) {
              <tr>
                <th scope="row">
                  {{ admin.firstName }} {{ admin.lastName }}
                  @if (isSelf(admin)) {
                    <span class="badge badge-muted ml-1">{{ text.self }}</span>
                  }
                  <span class="block text-xs font-normal break-all text-muted-foreground">{{
                    admin.email
                  }}</span>
                </th>
                <td>
                  <span
                    class="badge"
                    [class.badge-success]="admin.status === 'Active'"
                    [class.badge-muted]="admin.status !== 'Active'"
                  >
                    {{ text.statuses[admin.status] ?? admin.status }}
                  </span>
                </td>
                <td>{{ admin.mfaEnabled ? text.mfaOn : text.mfaOff }}</td>
                <td>
                  @if (isSelf(admin)) {
                    <span class="text-xs text-muted-foreground">{{ text.selfNote }}</span>
                  } @else if (admin.mfaEnabled) {
                    <button
                      type="button"
                      class="btn btn-secondary btn-sm"
                      [attr.aria-label]="resetLabel(admin)"
                      (click)="reset(admin)"
                    >
                      {{ mfa.action }}
                    </button>
                  }
                </td>
              </tr>
            }
          </tbody>
        </table>
      </div>
    }
  `,
})
export class PlatformAdminsPage {
  private readonly mfaService = inject(MfaService);
  private readonly auth = inject(AuthService);
  private readonly dialog = inject(Dialog);
  private readonly toasts = inject(ToastService);

  protected readonly text = ui.mfa.platformAdmins;
  protected readonly mfa = ui.mfa.reset;
  protected readonly common = ui.common;
  protected readonly admins = signal<PlatformAdminSummary[] | null>(null);
  protected readonly loadError = signal<string | null>(null);

  constructor() {
    this.load();
  }

  protected load(): void {
    this.loadError.set(null);
    this.mfaService.platformAdmins().subscribe({
      next: (admins) => this.admins.set(admins),
      error: (err: unknown) => this.loadError.set(problemMessage(toProblem(err))),
    });
  }

  protected isSelf(admin: PlatformAdminSummary): boolean {
    return admin.id === this.auth.user()?.id;
  }

  protected resetLabel(admin: PlatformAdminSummary): string {
    return interpolate(`${this.mfa.action}: {name}`, {
      name: `${admin.firstName} ${admin.lastName}`,
    });
  }

  protected async reset(admin: PlatformAdminSummary): Promise<void> {
    const ref = this.dialog.open<boolean, ResetMfaData>(ResetMfaDialog, {
      data: { userId: admin.id, name: `${admin.firstName} ${admin.lastName}` },
      ariaLabelledBy: RESET_MFA_TITLE_ID,
      backdropClass: ['bg-slate-900/60'],
    });
    if ((await firstValueFrom(ref.closed)) === true) {
      this.toasts.success(this.mfa.done);
      this.load();
    }
  }
}
