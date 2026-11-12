import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  inject,
  signal,
} from '@angular/core';

import { Api } from '../../api/api';
import {
  apiV1EnrollmentsIdEndPost,
  apiV1EnrollmentsPost,
  apiV1GroupsGet,
  apiV1GroupsIdEnrollmentsGet,
  apiV1PaymentsGet,
  apiV1StudentsGet,
} from '../../api/functions';
import { EnrollmentDto, GroupDto, PaymentDto, StudentListItemDto } from '../../api/models';
import { problemMessage, toProblem } from '../../core/api/problem';
import { formatDate } from '../../core/format/format';
import { errorMessages, interpolate, ui } from '../../core/i18n/tr';
import { SchoolClock } from '../../core/time/school-clock';
import { periodLabel } from '../billing/billing-format';
import { EnrollTarget, enrollProblem } from './enrollment-errors';
import { GroupChangeHint } from './group-change-hint';

export const ENROLL_TITLE_ID = 'enroll-title';
export const END_ENROLLMENT_TITLE_ID = 'end-enrollment-title';

/** Typing pause before a student search is sent. */
export const STUDENT_SEARCH_DEBOUNCE_MS = 300;

const dialogClass =
  'max-h-[calc(100dvh-2rem)] w-[min(32rem,calc(100vw-2rem))] overflow-y-auto rounded-lg border border-border bg-background p-6 shadow-lg';

/** Active enrollments of a group (the used part of its capacity). */
export async function activeCount(api: Api, groupId: string): Promise<number> {
  const page = await api.invoke(apiV1GroupsIdEnrollmentsGet, {
    id: groupId,
    status: 'Active',
    page: 1,
    pageSize: 1,
  });
  return Number(page.totalCount);
}

/** "Doğum yılı 2014–2015 · Kontenjan 12 / 16 (4 boş)". */
export function groupFacts(group: GroupDto, used: number): string {
  const capacity = Number(group.capacity);
  return interpolate(ui.enrollments.groupFacts, {
    from: group.birthYearRange.from,
    to: group.birthYearRange.to,
    used,
    capacity,
    free: Math.max(0, capacity - used),
  });
}

export interface EnrollStudentData {
  student: { id: string; name: string };
}

export interface EnrollGroupData {
  group: GroupDto;
}

/**
 * Enrolls a student (US-11). Opened from a student, the user picks an active group; opened from a
 * group, the user searches for an active student. Closes with the new enrollment.
 */
@Component({
  selector: 'app-enroll-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <form [class]="dialogClass" (submit)="$event.preventDefault(); save()" novalidate>
      <h2 [id]="titleId" class="text-lg font-semibold">{{ title }}</h2>
      @if (fixedStudent) {
        <p class="mt-1 text-sm text-muted-foreground">{{ fixedStudent.name }}</p>
      }
      @if (error()) {
        <p
          id="enroll-error"
          role="alert"
          tabindex="-1"
          class="mt-3 rounded-lg border border-destructive/30 bg-destructive/10 p-3 text-sm text-destructive"
        >
          {{ error() }}
        </p>
      }

      @if (fixedGroup) {
        <div class="mt-2 text-sm text-muted-foreground" aria-live="polite">
          <span class="font-medium text-foreground">{{ fixedGroup.name }}</span> ·
          {{ factsText() }}
        </div>
        @if (factsFailed()) {
          <button type="button" class="btn btn-ghost btn-sm mt-1" (click)="retryFacts()">
            {{ common.retry }}
          </button>
        }
        <div class="mt-4">
          <label class="field-label" for="enroll-search">{{ text.searchStudent }}</label>
          <input
            id="enroll-search"
            type="search"
            class="input"
            autocomplete="off"
            maxlength="100"
            aria-describedby="enroll-search-hint"
            [value]="searchText()"
            (input)="onSearch($any($event.target).value)"
          />
          <p id="enroll-search-hint" class="mt-1.5 text-xs text-muted-foreground">
            {{ text.searchStudentHint }}
          </p>
        </div>
        <fieldset class="mt-3 min-w-0" id="enroll-student" tabindex="-1">
          <legend class="field-label">{{ text.searchResults }}</legend>
          <div aria-live="polite">
            @if (searchError(); as message) {
              <p class="text-sm text-destructive">{{ message }}</p>
            } @else if (results() !== null) {
              <p [class.sr-only]="results()!.length > 0" class="text-sm text-muted-foreground">
                {{ results()!.length === 0 ? text.noStudentFound : resultCount() }}
              </p>
            }
          </div>
          <ul class="space-y-1">
            @for (s of results() ?? []; track s.id) {
              <li>
                <label
                  class="flex min-h-11 cursor-pointer items-center gap-3 rounded-lg border border-border px-3 py-2 text-sm hover:bg-muted/50"
                  [class.border-primary]="studentId() === s.id"
                >
                  <input
                    type="radio"
                    name="enroll-student"
                    class="size-4 accent-[var(--primary)]"
                    [value]="s.id"
                    [checked]="studentId() === s.id"
                    [attr.aria-invalid]="!!targetError('student')"
                    [attr.aria-describedby]="targetError('student') ? 'enroll-student-error' : null"
                    (change)="chooseStudent(s.id)"
                  />
                  <span class="min-w-0 flex-1">
                    {{ s.firstName }} {{ s.lastName }}
                    <span class="block font-mono text-xs text-muted-foreground">{{
                      date(s.birthDate)
                    }}</span>
                  </span>
                </label>
              </li>
            }
          </ul>
          @if (targetError('student'); as m) {
            <p id="enroll-student-error" class="field-error">{{ m }}</p>
          }
        </fieldset>
      } @else {
        <div class="mt-4">
          <label class="field-label" for="enroll-group">{{ text.chooseGroup }}</label>
          <select
            id="enroll-group"
            class="input"
            [attr.aria-invalid]="!!targetError('group')"
            [attr.aria-describedby]="
              'enroll-group-facts' + (targetError('group') ? ' enroll-group-error' : '')
            "
            (change)="chooseGroup($any($event.target).value)"
          >
            <option value="" [selected]="!groupId()">{{ text.chooseGroupPlaceholder }}</option>
            @for (g of groups(); track g.id) {
              <option [value]="g.id" [selected]="groupId() === g.id">{{ g.name }}</option>
            }
          </select>
          <p
            id="enroll-group-facts"
            class="mt-1.5 text-xs text-muted-foreground"
            aria-live="polite"
          >
            @if (selectedGroup()) {
              {{ factsText() }}
              @if (isFull()) {
                <span class="badge badge-warning ml-1">{{ text.full }}</span>
              }
            }
          </p>
          @if (selectedGroup() && factsFailed()) {
            <button type="button" class="btn btn-ghost btn-sm mt-1" (click)="retryFacts()">
              {{ common.retry }}
            </button>
          }
          @if (moreGroupsHint(); as hint) {
            <p class="mt-1.5 text-xs text-muted-foreground">{{ hint }}</p>
          }
          @if (targetError('group'); as m) {
            <p id="enroll-group-error" class="field-error">{{ m }}</p>
          }
        </div>
      }

      <div class="mt-4">
        <label class="field-label" for="enroll-start">{{ text.startDateLabel }}</label>
        <input
          id="enroll-start"
          type="date"
          class="input"
          [max]="today()"
          [value]="startDate()"
          (input)="startDate.set($any($event.target).value); clearTarget('startDate')"
          [attr.aria-invalid]="!!targetError('startDate')"
          [attr.aria-describedby]="
            'enroll-start-hint enroll-group-change-hint' +
            (targetError('startDate') ? ' enroll-start-error' : '')
          "
        />
        <p id="enroll-start-hint" class="mt-1.5 text-xs text-muted-foreground">
          {{ text.startDateHint }}
        </p>
        <p id="enroll-group-change-hint" class="mt-1 text-xs text-muted-foreground">
          {{ groupChangeHint.text() }}
        </p>
        @if (targetError('startDate'); as m) {
          <p id="enroll-start-error" class="field-error">{{ m }}</p>
        }
      </div>

      <div class="mt-6 flex flex-wrap justify-end gap-2">
        <button type="button" class="btn btn-secondary" (click)="ref.close()">
          {{ actions.cancel }}
        </button>
        <button type="submit" class="btn btn-primary" [disabled]="saving()">
          {{ title }}
        </button>
      </div>
    </form>
  `,
})
export class EnrollDialog {
  private readonly api = inject(Api);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly injector = inject(Injector);
  protected readonly ref = inject<DialogRef<EnrollmentDto>>(DialogRef);
  private readonly data = inject<EnrollStudentData | EnrollGroupData>(DIALOG_DATA);

  protected readonly fixedStudent = 'student' in this.data ? this.data.student : null;
  protected readonly fixedGroup = 'group' in this.data ? this.data.group : null;
  protected readonly dialogClass = dialogClass;
  protected readonly titleId = ENROLL_TITLE_ID;
  protected readonly text = ui.enrollments;
  protected readonly actions = ui.actions;
  protected readonly common = ui.common;
  protected readonly title = this.fixedGroup
    ? ui.enrollments.enrollStudentTitle
    : ui.enrollments.enrollTitle;

  protected readonly today = inject(SchoolClock).today;
  protected readonly startDate = signal(this.today());
  protected readonly groupChangeHint = new GroupChangeHint(inject(Api), this.startDate);
  protected readonly groups = signal<GroupDto[]>([]);
  protected readonly groupId = signal(this.fixedGroup?.id ?? '');
  protected readonly selectedGroup = computed(
    () => this.fixedGroup ?? this.groups().find((g) => g.id === this.groupId()) ?? null,
  );
  protected readonly used = signal<number | null>(null);
  protected readonly factsFailed = signal(false);
  /** Shown when the school has more active groups than the select lists. */
  protected readonly moreGroupsHint = signal<string | null>(null);
  protected readonly facts = computed(() => {
    const group = this.selectedGroup();
    const used = this.used();
    return group && used !== null ? groupFacts(group, used) : null;
  });
  protected readonly factsText = computed(
    () =>
      this.facts() ??
      (this.factsFailed() ? this.text.groupFactsFailed : this.text.groupFactsLoading),
  );
  protected readonly isFull = computed(() => {
    const group = this.selectedGroup();
    const used = this.used();
    return !!group && used !== null && used >= Number(group.capacity);
  });

  protected readonly searchText = signal('');
  protected readonly results = signal<StudentListItemDto[] | null>(null);
  protected readonly studentId = signal(this.fixedStudent?.id ?? '');
  protected readonly searchError = signal<string | null>(null);
  protected readonly resultCount = computed(() =>
    interpolate(this.text.resultCount, { count: this.results()?.length ?? 0 }),
  );

  protected readonly saving = signal(false);
  protected readonly error = signal<string | null>(null);
  private readonly errors = signal<Partial<Record<Exclude<EnrollTarget, null>, string>>>({});
  private factsSequence = 0;
  private searchSequence = 0;
  private debounce: ReturnType<typeof setTimeout> | null = null;

  constructor() {
    if (this.fixedGroup) {
      void this.loadFacts(this.fixedGroup.id);
    } else {
      void this.loadGroups();
    }
    inject(DestroyRef).onDestroy(() => {
      if (this.debounce) {
        clearTimeout(this.debounce);
      }
    });
  }

  /** Null only for an anonymized student, which the active-student search never returns (DEC-363). */
  protected date(iso: string | null): string {
    return formatDate(iso);
  }

  protected targetError(target: Exclude<EnrollTarget, null>): string | undefined {
    return this.errors()[target];
  }

  protected clearTarget(target: Exclude<EnrollTarget, null>): void {
    if (this.errors()[target]) {
      this.errors.update((all) => ({ ...all, [target]: undefined }));
    }
  }

  protected chooseGroup(id: string): void {
    this.groupId.set(id);
    this.clearTarget('group');
    this.used.set(null);
    if (id) {
      void this.loadFacts(id);
    }
  }

  protected retryFacts(): void {
    const group = this.selectedGroup();
    if (group) {
      void this.loadFacts(group.id);
    }
  }

  protected chooseStudent(id: string): void {
    this.studentId.set(id);
    this.clearTarget('student');
  }

  protected onSearch(value: string): void {
    this.searchText.set(value);
    if (this.debounce) {
      clearTimeout(this.debounce);
    }
    this.debounce = setTimeout(() => {
      this.debounce = null;
      void this.search(value.trim());
    }, STUDENT_SEARCH_DEBOUNCE_MS);
  }

  protected async save(): Promise<void> {
    if (this.saving()) {
      return;
    }
    const groupId = this.groupId();
    const studentId = this.studentId();
    const start = this.startDate();
    const problems: Partial<Record<Exclude<EnrollTarget, null>, string>> = {};
    if (!groupId) {
      problems.group = errorMessages['Field.Required'];
    }
    if (!studentId) {
      problems.student = this.text.chooseStudent;
    }
    if (!start || start > this.today()) {
      problems.startDate = this.text.startDateHint;
    }
    if (Object.keys(problems).length > 0) {
      this.errors.set(problems);
      this.focusFirstProblem();
      return;
    }
    this.saving.set(true);
    this.error.set(null);
    this.errors.set({});
    try {
      const enrollment = await this.api.invoke(apiV1EnrollmentsPost, {
        body: { studentId, groupId, startDate: start },
      });
      this.ref.close(enrollment);
    } catch (err) {
      const mapped = enrollProblem(toProblem(err), this.fixedGroup ? 'student' : 'group');
      if (mapped.target) {
        this.errors.set({ [mapped.target]: mapped.message });
      } else {
        this.error.set(mapped.message);
      }
      this.saving.set(false);
      // A full group may have changed the numbers; show the current capacity.
      const group = this.selectedGroup();
      if (group) {
        void this.loadFacts(group.id);
      }
      this.focusFirstProblem();
    }
  }

  private focusFirstProblem(): void {
    afterNextRender(
      () => {
        const order = ['enroll-error', 'enroll-group', 'enroll-student', 'enroll-start'];
        const errors = this.errors();
        const target = order.find((id) =>
          id === 'enroll-error'
            ? !!this.error()
            : id === 'enroll-group'
              ? !!errors.group
              : id === 'enroll-student'
                ? !!errors.student
                : !!errors.startDate,
        );
        if (target) {
          this.host.nativeElement.querySelector<HTMLElement>(`#${target}`)?.focus();
        }
      },
      { injector: this.injector },
    );
  }

  private async loadGroups(): Promise<void> {
    try {
      const page = await this.api.invoke(apiV1GroupsGet, {
        status: 'Active',
        page: 1,
        pageSize: 100,
      });
      this.groups.set(page.items);
      const total = Number(page.totalCount);
      this.moreGroupsHint.set(
        total > page.items.length
          ? interpolate(this.text.moreGroups, { shown: page.items.length, total })
          : null,
      );
    } catch (err) {
      this.error.set(problemMessage(toProblem(err)));
    }
  }

  private async loadFacts(groupId: string): Promise<void> {
    const request = ++this.factsSequence;
    this.factsFailed.set(false);
    try {
      const used = await activeCount(this.api, groupId);
      if (request === this.factsSequence) {
        this.used.set(used);
      }
    } catch {
      // The numbers are a convenience (the API enforces the capacity); offer a retry.
      if (request === this.factsSequence) {
        this.used.set(null);
        this.factsFailed.set(true);
      }
    }
  }

  private async search(term: string): Promise<void> {
    const request = ++this.searchSequence;
    if (term.length < 2) {
      // Too short to search: nothing listed, so nothing may stay selected either.
      this.results.set(null);
      this.studentId.set('');
      this.searchError.set(null);
      return;
    }
    try {
      const page = await this.api.invoke(apiV1StudentsGet, {
        search: term,
        status: 'Active',
        page: 1,
        pageSize: 10,
      });
      if (request === this.searchSequence) {
        this.searchError.set(null);
        this.results.set(page.items);
        if (!page.items.some((s) => s.id === this.studentId())) {
          this.studentId.set('');
        }
      }
    } catch (err) {
      if (request === this.searchSequence) {
        this.results.set(null);
        this.studentId.set('');
        this.searchError.set(problemMessage(toProblem(err)));
      }
    }
  }
}

/** What the end dialog closes with when someone else ended the enrollment first. */
export const ALREADY_ENDED = 'alreadyEnded' as const;
export type EndEnrollmentResult = EnrollmentDto | typeof ALREADY_ENDED;

export interface EndEnrollmentData {
  enrollment: EnrollmentDto;
}

/** Ends an enrollment on a date between its start and today; closes with the updated enrollment. */
@Component({
  selector: 'app-end-enrollment-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <form [class]="dialogClass" (submit)="$event.preventDefault(); save()" novalidate>
      <h2 [id]="titleId" class="text-lg font-semibold">{{ text.endTitle }}</h2>
      <p class="mt-2 text-sm text-muted-foreground">{{ description }}</p>
      @if (error()) {
        <p
          role="alert"
          class="mt-3 rounded-lg border border-destructive/30 bg-destructive/10 p-3 text-sm text-destructive"
        >
          {{ error() }}
        </p>
      }
      <div class="mt-4">
        <label class="field-label" for="end-date">{{ text.endDateLabel }}</label>
        <input
          id="end-date"
          type="date"
          class="input"
          [min]="enrollment.startDate"
          [max]="today()"
          [value]="date()"
          (input)="date.set($any($event.target).value); dateError.set(null)"
          [attr.aria-invalid]="!!dateError()"
          [attr.aria-describedby]="
            'end-date-hint end-group-change-hint' + (dateError() ? ' end-date-error' : '')
          "
        />
        <p id="end-date-hint" class="mt-1.5 text-xs text-muted-foreground">
          {{ text.endDateHint }}
        </p>
        <p id="end-group-change-hint" class="mt-1 text-xs text-muted-foreground">
          {{ groupChangeHint.text() }}
        </p>
        @if (dateError(); as m) {
          <p id="end-date-error" class="field-error">{{ m }}</p>
        }
      </div>
      @if (prepaidWarning(); as warning) {
        <p
          id="end-prepaid-warning"
          role="note"
          class="mt-4 rounded-lg border border-warning/40 bg-warning/10 p-3 text-sm"
        >
          {{ warning }}
        </p>
      }
      <div class="mt-6 flex flex-wrap justify-end gap-2">
        <button type="button" class="btn btn-secondary" (click)="ref.close()">
          {{ actions.cancel }}
        </button>
        <button type="submit" class="btn btn-destructive" [disabled]="saving()">
          {{ text.end }}
        </button>
      </div>
    </form>
  `,
})
export class EndEnrollmentDialog {
  private readonly api = inject(Api);
  protected readonly ref = inject<DialogRef<EndEnrollmentResult>>(DialogRef);
  protected readonly enrollment = inject<EndEnrollmentData>(DIALOG_DATA).enrollment;

  protected readonly dialogClass = dialogClass;
  protected readonly titleId = END_ENROLLMENT_TITLE_ID;
  protected readonly text = ui.enrollments;
  protected readonly actions = ui.actions;
  protected readonly description = interpolate(ui.enrollments.endText, {
    student: `${this.enrollment.studentFirstName} ${this.enrollment.studentLastName}`,
    group: this.enrollment.groupName,
  });
  protected readonly today = inject(SchoolClock).today;
  protected readonly date = signal(this.today());
  protected readonly groupChangeHint = new GroupChangeHint(this.api, this.date);
  protected readonly dateError = signal<string | null>(null);
  protected readonly saving = signal(false);
  protected readonly error = signal<string | null>(null);
  /** Recorded payments of this enrollment (DEC-422); empty when they could not be read. */
  private readonly payments = signal<PaymentDto[]>([]);
  /**
   * P3-13 (DEC-184, DEC-422): months paid in advance beyond the end month. Ending does not refund them; the warning
   * points to the refund (online payments) or a refund by hand (cash and transfer). Warning only.
   */
  protected readonly prepaidWarning = computed(() => {
    const endMonth = (this.date() || this.today()).slice(0, 7);
    const months = [
      ...new Set(
        this.payments()
          .filter((p) => p.enrollmentId === this.enrollment.id && p.period > endMonth)
          .map((p) => p.period),
      ),
    ].sort();
    return months.length === 0
      ? null
      : interpolate(this.text.prepaidWarning, { months: months.map(periodLabel).join(', ') });
  });

  constructor() {
    void this.loadPayments();
  }

  private async loadPayments(): Promise<void> {
    try {
      const page = await this.api.invoke(apiV1PaymentsGet, {
        studentId: this.enrollment.studentId,
        status: 'Recorded',
        page: 1,
        pageSize: 100,
      });
      this.payments.set(page.items);
    } catch {
      // The warning is a convenience; ending works without it.
    }
  }

  protected async save(): Promise<void> {
    if (this.saving()) {
      return;
    }
    const date = this.date();
    if (!date || date > this.today() || date < this.enrollment.startDate) {
      this.dateError.set(this.text.endDateHint);
      return;
    }
    this.saving.set(true);
    this.error.set(null);
    try {
      const ended = await this.api.invoke(apiV1EnrollmentsIdEndPost, {
        id: this.enrollment.id,
        body: { date },
      });
      this.ref.close(ended);
    } catch (err) {
      const problem = toProblem(err);
      if (problem.code === 'Enrollment.AlreadyEnded') {
        // Nothing left to do here; the page reloads and says so.
        this.ref.close(ALREADY_ENDED);
        return;
      }
      if (problem.code === 'Enrollment.InvalidEndDate' || problem.errors?.['date']) {
        this.dateError.set(
          problem.code === 'Enrollment.InvalidEndDate'
            ? problemMessage(problem)
            : this.text.endDateHint,
        );
      } else {
        this.error.set(problemMessage(problem));
      }
      this.saving.set(false);
    }
  }
}
