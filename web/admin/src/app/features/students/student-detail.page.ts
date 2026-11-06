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
import {
  LucideArrowLeft,
  LucideClipboardList,
  LucideLogOut,
  LucidePencil,
  LucideRotateCcw,
} from '@lucide/angular';
import { firstValueFrom } from 'rxjs';

import { Api } from '../../api/api';
import { apiV1StudentsIdGet, apiV1StudentsIdReactivatePost } from '../../api/functions';
import { GuardianDto, StudentDto, StudentGuardianDto } from '../../api/models';
import { problemMessage, toProblem } from '../../core/api/problem';
import { formatDate, formatPhone } from '../../core/format/format';
import { interpolate, ui } from '../../core/i18n/tr';
import { ageOn, todayIso } from '../../core/students/age';
import { ConfirmService } from '../../shared/ui/confirm-dialog.component';
import { PageHeaderComponent } from '../../shared/ui/page-header.component';
import { ToastService } from '../../shared/ui/toast.service';
import {
  GUARDIAN_EDIT_TITLE_ID,
  GuardianEditData,
  GuardianEditDialog,
  LEAVE_TITLE_ID,
  STUDENT_EDIT_TITLE_ID,
  StudentEditDialog,
  StudentLeaveDialog,
} from './student-dialogs';

/** US-10: one student — profile, guardians, emergency contact, leave and reactivate. */
@Component({
  selector: 'app-student-detail-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    RouterLink,
    PageHeaderComponent,
    LucideArrowLeft,
    LucideClipboardList,
    LucideLogOut,
    LucidePencil,
    LucideRotateCcw,
  ],
  template: `
    <a routerLink="/students" class="btn btn-ghost btn-sm mb-2 -ml-2">
      <svg lucideArrowLeft></svg>{{ text.back }}
    </a>

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
    } @else if (!student()) {
      <p role="status" class="card p-6 text-sm text-muted-foreground">{{ common.loading }}</p>
    } @else {
      @let s = student()!;
      <app-page-header [title]="s.firstName + ' ' + s.lastName">
        <span
          class="badge"
          [class.badge-success]="s.status === 'Active'"
          [class.badge-muted]="s.status !== 'Active'"
        >
          {{ list.statuses[s.status] }}
        </span>
        <button type="button" class="btn btn-secondary" [disabled]="busy()" (click)="edit()">
          <svg lucidePencil></svg>{{ text.edit }}
        </button>
        @if (s.status === 'Active') {
          <button type="button" class="btn btn-outline" [disabled]="busy()" (click)="leave()">
            <svg lucideLogOut></svg>{{ text.leave }}
          </button>
        } @else {
          <button type="button" class="btn btn-outline" [disabled]="busy()" (click)="reactivate()">
            <svg lucideRotateCcw></svg>{{ text.reactivate }}
          </button>
        }
      </app-page-header>

      <div class="grid gap-4 lg:grid-cols-2">
        <section class="card p-4" aria-labelledby="profile-title">
          <h2 id="profile-title" class="mb-3 text-base font-semibold">{{ text.profile }}</h2>
          <dl class="grid grid-cols-2 gap-3 text-sm">
            <div>
              <dt class="text-xs text-muted-foreground">{{ list.birthDate }}</dt>
              <dd class="font-mono">
                {{ date(s.birthDate) }}
                <span class="font-sans text-muted-foreground">{{ age() }}</span>
              </dd>
            </div>
            <div>
              <dt class="text-xs text-muted-foreground">{{ list.gender }}</dt>
              <dd>{{ list.genders[s.gender] }}</dd>
            </div>
            @if (s.leftAt) {
              <div>
                <dt class="text-xs text-muted-foreground">{{ list.leftAt }}</dt>
                <dd class="font-mono">{{ date(s.leftAt) }}</dd>
              </div>
            }
          </dl>
          <h3 class="mt-4 mb-2 text-sm font-semibold">{{ text.contact }}</h3>
          <p class="font-mono text-sm">{{ s.phone ? phone(s.phone) : text.noPhone }}</p>
          <p class="text-sm break-all">{{ s.email ?? text.noEmail }}</p>
        </section>

        <section class="card p-4" aria-labelledby="emergency-title">
          <h2 id="emergency-title" class="mb-3 text-base font-semibold">{{ text.emergency }}</h2>
          <p class="text-sm font-medium">{{ s.emergencyContact.name }}</p>
          <p class="text-sm text-muted-foreground">{{ s.emergencyContact.relationship }}</p>
          <p class="font-mono text-sm">{{ phone(s.emergencyContact.phone) }}</p>
        </section>

        <section class="card p-4" aria-labelledby="guardians-title">
          <h2 id="guardians-title" class="mb-3 text-base font-semibold">{{ text.guardians }}</h2>
          @if (s.guardians.length === 0) {
            <p class="text-sm text-muted-foreground">{{ text.noGuardians }}</p>
          }
          <ul class="divide-y divide-border/60">
            @for (g of s.guardians; track g.guardianId) {
              <li class="flex items-start justify-between gap-3 py-2">
                <div class="min-w-0 text-sm">
                  <p class="font-medium">
                    {{ g.firstName }} {{ g.lastName }}
                    <span class="font-normal text-muted-foreground"
                      >· {{ relationships[g.relationship] }}</span
                    >
                  </p>
                  <p class="font-mono text-xs">{{ phone(g.phone) }}</p>
                  @if (g.email) {
                    <p class="text-xs break-all text-muted-foreground">{{ g.email }}</p>
                  }
                </div>
                <button
                  type="button"
                  class="btn btn-ghost btn-sm btn-icon shrink-0"
                  [attr.aria-label]="text.editGuardian + ': ' + g.firstName + ' ' + g.lastName"
                  [attr.title]="text.editGuardian"
                  [disabled]="busy()"
                  (click)="editGuardian(g)"
                >
                  <svg lucidePencil></svg>
                </button>
              </li>
            }
          </ul>
        </section>

        <section class="card p-4" aria-labelledby="enrollments-title">
          <h2 id="enrollments-title" class="mb-3 text-base font-semibold">
            {{ text.enrollments }}
          </h2>
          <div class="flex items-center gap-3 text-sm text-muted-foreground">
            <svg lucideClipboardList class="size-5 shrink-0" aria-hidden="true"></svg>
            {{ text.enrollmentsSoon }}
          </div>
        </section>
      </div>
    }
  `,
})
export class StudentDetailPage {
  private readonly api = inject(Api);
  private readonly dialog = inject(Dialog);
  private readonly toasts = inject(ToastService);
  private readonly confirmer = inject(ConfirmService);

  /** Route parameter (component input binding). */
  readonly id = input.required<string>();

  protected readonly text = ui.studentDetail;
  protected readonly list = ui.students;
  protected readonly relationships = ui.studentForm.relationships;
  protected readonly common = ui.common;
  protected readonly student = signal<StudentDto | null>(null);
  protected readonly loadError = signal<string | null>(null);
  protected readonly busy = signal(false);
  protected readonly age = computed(() => {
    const s = this.student();
    const age = s ? ageOn(s.birthDate, todayIso()) : null;
    return age === null ? '' : `(${interpolate(this.list.age, { age })})`;
  });

  constructor() {
    effect(() => {
      this.id();
      untracked(() => void this.load());
    });
  }

  protected date(iso: string): string {
    return formatDate(iso);
  }

  protected phone(value: string): string {
    return formatPhone(value);
  }

  async load(): Promise<void> {
    this.loadError.set(null);
    try {
      this.student.set(await this.api.invoke(apiV1StudentsIdGet, { id: this.id() }));
    } catch (err) {
      this.loadError.set(problemMessage(toProblem(err)));
    }
  }

  protected async edit(): Promise<void> {
    const student = this.student();
    if (!student) {
      return;
    }
    await this.exclusive(async () => {
      const saved = await this.open<StudentDto>(StudentEditDialog, student, STUDENT_EDIT_TITLE_ID);
      if (saved) {
        this.student.set(saved);
        this.toasts.success(this.text.saved);
      } else {
        // A conflict inside the dialog may have changed the data; show the current state.
        await this.load();
      }
    });
  }

  protected async editGuardian(guardian: StudentGuardianDto): Promise<void> {
    await this.exclusive(async () => {
      const data: GuardianEditData = { phone: guardian.phone };
      const saved = await this.open<GuardianDto>(GuardianEditDialog, data, GUARDIAN_EDIT_TITLE_ID);
      if (saved) {
        this.toasts.success(this.text.guardianSaved);
      }
      await this.load();
    });
  }

  protected async leave(): Promise<void> {
    const student = this.student();
    if (!student) {
      return;
    }
    await this.exclusive(async () => {
      const updated = await this.open<StudentDto>(StudentLeaveDialog, student, LEAVE_TITLE_ID);
      if (updated) {
        this.student.set(updated);
        this.toasts.success(this.text.left);
      }
    });
  }

  protected async reactivate(): Promise<void> {
    const student = this.student();
    if (!student) {
      return;
    }
    await this.exclusive(async () => {
      const ok = await this.confirmer.confirm({
        title: this.text.reactivateTitle,
        message: this.text.reactivateText,
        confirmLabel: this.text.reactivate,
      });
      if (!ok) {
        return;
      }
      try {
        this.student.set(await this.api.invoke(apiV1StudentsIdReactivatePost, { id: student.id }));
        this.toasts.success(this.text.reactivated);
      } catch (err) {
        this.toasts.error(problemMessage(toProblem(err)));
        await this.load();
      }
    });
  }

  private async open<R>(
    component: Parameters<Dialog['open']>[0],
    data: unknown,
    titleId: string,
  ): Promise<R | undefined> {
    const ref = this.dialog.open<R>(component, {
      data,
      ariaLabelledBy: titleId,
      backdropClass: ['bg-slate-900/60'],
    });
    return firstValueFrom(ref.closed);
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
