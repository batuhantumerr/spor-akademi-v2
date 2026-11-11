import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import { LucideMail, LucideMailX } from '@lucide/angular';

import { Api } from '../../api/api';
import {
  apiV1GuardiansIdInvitationDelete,
  apiV1GuardiansIdInvitationPost,
} from '../../api/functions';
import { StudentGuardianDto } from '../../api/models';
import { problemMessage, toProblem } from '../../core/api/problem';
import { formatDate } from '../../core/format/format';
import { interpolate, ui } from '../../core/i18n/tr';
import { SchoolClock, dateIn } from '../../core/time/school-clock';
import { ConfirmService } from '../../shared/ui/confirm-dialog.component';
import { ToastService } from '../../shared/ui/toast.service';

/**
 * P3-02 (DEC-382): one guardian's parent account on the student page — its status (Davet edilmedi / Davet gönderildi /
 * Aktif / İptal) and the invite, resend and revoke actions. The API re-checks every rule; this only offers what fits.
 */
@Component({
  selector: 'app-guardian-access',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [LucideMail, LucideMailX],
  template: `
    @let g = guardian();
    @let status = g.invitation.status;
    <div class="mt-1 flex flex-wrap items-center gap-2">
      <span
        class="badge"
        [class.badge-success]="status === 'Active'"
        [class.badge-warning]="status === 'Invited'"
        [class.badge-muted]="status === 'NotInvited' || status === 'Revoked'"
        [attr.data-testid]="'invitation-status-' + g.guardianId"
      >
        {{ text.statuses[status] }}
      </span>
      @if (validity(); as note) {
        <span class="text-xs text-muted-foreground">{{ note }}</span>
      }
    </div>
    <div class="mt-1 flex flex-wrap gap-1">
      @if (status === 'Active' || status === 'Invited') {
        @if (status === 'Invited') {
          <button
            type="button"
            class="btn btn-ghost btn-sm"
            [disabled]="busy() || disabled()"
            (click)="invite()"
          >
            <svg lucideMail></svg>{{ text.resend }}
          </button>
        }
        <button
          type="button"
          class="btn btn-ghost btn-sm text-destructive"
          [disabled]="busy() || disabled()"
          (click)="revoke()"
        >
          <svg lucideMailX></svg>{{ status === 'Active' ? text.close : text.revoke }}
        </button>
      } @else {
        <button
          type="button"
          class="btn btn-ghost btn-sm"
          [disabled]="busy() || disabled() || !g.email"
          [attr.title]="g.email ? null : text.needsEmail"
          (click)="invite()"
        >
          <svg lucideMail></svg>{{ status === 'Revoked' ? text.reinvite : text.invite }}
        </button>
        @if (!g.email) {
          <span class="text-xs text-muted-foreground">{{ text.needsEmail }}</span>
        }
      }
    </div>
  `,
})
export class GuardianAccessComponent {
  private readonly api = inject(Api);
  private readonly toasts = inject(ToastService);
  private readonly confirmer = inject(ConfirmService);
  private readonly clock = inject(SchoolClock);

  readonly guardian = input.required<StudentGuardianDto>();
  /** Another action of the page is running. */
  readonly disabled = input(false);
  /** The account changed; the page reloads the student. */
  readonly changed = output<void>();

  protected readonly text = ui.parentAccess;
  protected readonly busy = signal(false);

  /** "dd.MM.yyyy tarihine kadar geçerli" for a pending link, or that it expired. */
  protected readonly validity = computed(() => {
    const invitation = this.guardian().invitation;
    if (invitation.status !== 'Invited') {
      return null;
    }
    if (!invitation.invitationExpiresAt) {
      return this.text.sending;
    }
    const expires = new Date(invitation.invitationExpiresAt);
    return expires.getTime() <= Date.now()
      ? this.text.expired
      : interpolate(this.text.validUntil, {
          date: formatDate(dateIn(expires, this.clock.timeZone())),
        });
  });

  protected async invite(): Promise<void> {
    const g = this.guardian();
    await this.run(async () => {
      await this.api.invoke(apiV1GuardiansIdInvitationPost, { id: g.guardianId });
      const message = g.invitation.status === 'Invited' ? this.text.resent : this.text.sent;
      this.toasts.success(interpolate(message, { email: g.email ?? '' }));
    });
  }

  protected async revoke(): Promise<void> {
    const g = this.guardian();
    const active = g.invitation.status === 'Active';
    const ok = await this.confirmer.confirm({
      title: active ? this.text.closeTitle : this.text.revokeTitle,
      message: interpolate(active ? this.text.closeText : this.text.revokeText, {
        name: `${g.firstName} ${g.lastName}`,
      }),
      confirmLabel: active ? this.text.close : this.text.revoke,
      destructive: true,
    });
    if (!ok) {
      return;
    }
    await this.run(async () => {
      await this.api.invoke(apiV1GuardiansIdInvitationDelete, { id: g.guardianId });
      this.toasts.success(active ? this.text.closed : this.text.revoked);
    });
  }

  private async run(work: () => Promise<void>): Promise<void> {
    if (this.busy()) {
      return;
    }
    this.busy.set(true);
    try {
      await work();
    } catch (err) {
      this.toasts.error(problemMessage(toProblem(err)));
    } finally {
      this.busy.set(false);
      // Success or not, the server's state is the truth (e.g. a concurrent change).
      this.changed.emit();
    }
  }
}
