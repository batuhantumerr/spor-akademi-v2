import { Dialog } from '@angular/cdk/dialog';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  signal,
  untracked,
} from '@angular/core';
import { RouterLink } from '@angular/router';
import { LucideClipboardList, LucidePlus } from '@lucide/angular';
import { firstValueFrom } from 'rxjs';

import { Api } from '../../api/api';
import { apiV1StudentsIdEnrollmentsGet } from '../../api/functions';
import { EnrollmentDto } from '../../api/models';
import { problemMessage, toProblem } from '../../core/api/problem';
import { formatDate } from '../../core/format/format';
import { errorMessages, ui } from '../../core/i18n/tr';
import { ToastService } from '../../shared/ui/toast.service';
import {
  ALREADY_ENDED,
  END_ENROLLMENT_TITLE_ID,
  ENROLL_TITLE_ID,
  EndEnrollmentData,
  EndEnrollmentDialog,
  EndEnrollmentResult,
  EnrollDialog,
  EnrollStudentData,
} from './enrollment-dialogs';

/** "Kayıtlar" on the student page: active and ended enrollments, enroll and end actions. */
@Component({
  selector: 'app-student-enrollments',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, LucideClipboardList, LucidePlus],
  template: `
    <section class="card p-4" aria-labelledby="enrollments-title">
      <div class="mb-3 flex flex-wrap items-center justify-between gap-2">
        <h2 id="enrollments-title" class="text-base font-semibold">{{ text.title }}</h2>
        @if (studentActive()) {
          <button
            type="button"
            class="btn btn-secondary btn-sm"
            [disabled]="busy()"
            (click)="enroll()"
          >
            <svg lucidePlus></svg>{{ text.enroll }}
          </button>
        } @else {
          <p class="text-xs text-muted-foreground">{{ text.studentLeft }}</p>
        }
      </div>

      @if (error(); as message) {
        <div role="alert" class="flex items-center justify-between gap-3 text-sm text-destructive">
          <span>{{ message }}</span>
          <button type="button" class="btn btn-secondary btn-sm" (click)="load()">
            {{ common.retry }}
          </button>
        </div>
      } @else if (enrollments() === null) {
        <p role="status" class="text-sm text-muted-foreground">{{ common.loading }}</p>
      } @else if (enrollments()!.length === 0) {
        <div class="flex items-center gap-3 text-sm text-muted-foreground">
          <svg lucideClipboardList class="size-5 shrink-0" aria-hidden="true"></svg>
          {{ text.none }}
        </div>
      } @else {
        @for (block of blocks(); track block.title) {
          @if (block.items.length > 0) {
            <h3 class="mt-3 mb-1 text-xs font-bold tracking-wider text-muted-foreground uppercase">
              {{ block.title }}
            </h3>
            <ul class="divide-y divide-border/60">
              @for (e of block.items; track e.id) {
                <li class="flex flex-wrap items-center justify-between gap-2 py-2 text-sm">
                  <div class="min-w-0">
                    <a
                      class="font-medium text-primary underline-offset-4 hover:underline"
                      [routerLink]="['/groups', e.groupId]"
                      >{{ e.groupName }}</a
                    >
                    <p class="font-mono text-xs text-muted-foreground">
                      {{ date(e.startDate) }} – {{ e.endDate ? date(e.endDate) : '…' }}
                    </p>
                  </div>
                  @if (e.status === 'Active') {
                    <button
                      type="button"
                      class="btn btn-ghost btn-sm hover:!bg-destructive/10 hover:!text-destructive"
                      [attr.aria-label]="text.end + ': ' + e.groupName"
                      [disabled]="busy()"
                      (click)="end(e)"
                    >
                      {{ text.end }}
                    </button>
                  } @else {
                    <span class="badge badge-muted">{{ text.statuses[e.status] }}</span>
                  }
                </li>
              }
            </ul>
          }
        }
      }
    </section>
  `,
})
export class StudentEnrollmentsComponent {
  private readonly api = inject(Api);
  private readonly dialog = inject(Dialog);
  private readonly toasts = inject(ToastService);

  readonly studentId = input.required<string>();
  readonly studentName = input.required<string>();
  /** Students who left cannot be enrolled. */
  readonly studentActive = input(true);

  protected readonly text = ui.enrollments;
  protected readonly common = ui.common;
  protected readonly enrollments = signal<EnrollmentDto[] | null>(null);
  protected readonly error = signal<string | null>(null);
  protected readonly busy = signal(false);
  protected readonly blocks = computed(() => {
    const all = this.enrollments() ?? [];
    return [
      { title: this.text.active, items: all.filter((e) => e.status === 'Active') },
      { title: this.text.ended, items: all.filter((e) => e.status !== 'Active') },
    ];
  });
  private sequence = 0;

  constructor() {
    effect(() => {
      this.studentId();
      // Leaving ends the active enrollments on the server; reload when the status changes.
      this.studentActive();
      untracked(() => {
        this.enrollments.set(null);
        void this.load();
      });
    });
  }

  protected date(iso: string): string {
    return formatDate(iso);
  }

  async load(): Promise<void> {
    const request = ++this.sequence;
    this.error.set(null);
    try {
      const list = await this.api.invoke(apiV1StudentsIdEnrollmentsGet, { id: this.studentId() });
      if (request === this.sequence) {
        this.enrollments.set(list);
      }
    } catch (err) {
      if (request === this.sequence) {
        this.error.set(problemMessage(toProblem(err)));
      }
    }
  }

  protected async enroll(): Promise<void> {
    await this.exclusive(async () => {
      const data: EnrollStudentData = {
        student: { id: this.studentId(), name: this.studentName() },
      };
      const ref = this.dialog.open<EnrollmentDto>(EnrollDialog, {
        data,
        ariaLabelledBy: ENROLL_TITLE_ID,
        backdropClass: ['bg-slate-900/60'],
      });
      if (await firstValueFrom(ref.closed)) {
        this.toasts.success(this.text.enrolled);
      }
      // Reload whatever happened: someone else may have changed the enrollments meanwhile.
      await this.load();
    });
  }

  protected async end(enrollment: EnrollmentDto): Promise<void> {
    await this.exclusive(async () => {
      const data: EndEnrollmentData = { enrollment };
      const ref = this.dialog.open<EndEnrollmentResult>(EndEnrollmentDialog, {
        data,
        ariaLabelledBy: END_ENROLLMENT_TITLE_ID,
        backdropClass: ['bg-slate-900/60'],
      });
      const result = await firstValueFrom(ref.closed);
      if (result === ALREADY_ENDED) {
        this.toasts.error(errorMessages['Enrollment.AlreadyEnded']);
      } else if (result) {
        this.toasts.success(this.text.endedToast);
      }
      await this.load();
    });
  }

  private async exclusive(work: () => Promise<void>): Promise<void> {
    if (this.busy()) {
      return;
    }
    this.busy.set(true);
    try {
      await work();
    } finally {
      this.busy.set(false);
    }
  }
}
