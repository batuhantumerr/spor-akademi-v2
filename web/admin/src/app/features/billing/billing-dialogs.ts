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
  apiV1PaymentsIdCancelPost,
  apiV1PaymentsPost,
  apiV1StudentsGet,
  apiV1StudentsIdEnrollmentsGet,
} from '../../api/functions';
import { EnrollmentDto, PaymentDto, PaymentMethod, StudentListItemDto } from '../../api/models';
import { fieldErrors, problemMessage, toProblem } from '../../core/api/problem';
import { formatDate } from '../../core/format/format';
import { errorMessages, interpolate, ui } from '../../core/i18n/tr';
import { displayMoney, isPositive, money, parseMoneyInput } from '../../core/money/money';
import { SchoolClock } from '../../core/time/school-clock';
import { periodLabel } from './billing-format';

export const RECORD_PAYMENT_TITLE_ID = 'record-payment-title';
export const CANCEL_PAYMENT_TITLE_ID = 'cancel-payment-title';
export const PAYMENT_SEARCH_DEBOUNCE_MS = 300;

const dialogClass =
  'max-h-[calc(100dvh-2rem)] w-[min(32rem,calc(100vw-2rem))] overflow-y-auto rounded-lg border border-border bg-background p-6 shadow-lg';

/** Opening from an outstanding due fills enrollment, period and the remaining amount. */
export interface RecordPaymentData {
  prefill?: { enrollmentId: string; label: string; period: string; amount: string };
}

type Field = 'student' | 'enrollment' | 'period' | 'amount' | 'paidOn';

const fieldIds: Record<Field, string> = {
  student: 'pay-search',
  enrollment: 'pay-enrollment',
  period: 'pay-period',
  amount: 'pay-amount',
  paidOn: 'pay-paid-on',
};

/** US-17: record a payment for an enrollment and period; the amount is typed as text. */
@Component({
  selector: 'app-record-payment-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <form [class]="dialogClass" (submit)="$event.preventDefault(); save()" novalidate>
      <h2 [id]="titleId" class="text-lg font-semibold">{{ text.recordPaymentTitle }}</h2>
      @if (error()) {
        <p
          id="pay-error"
          role="alert"
          tabindex="-1"
          class="mt-3 rounded-lg border border-destructive/30 bg-destructive/10 p-3 text-sm text-destructive"
        >
          {{ error() }}
        </p>
      }

      <div class="mt-4 space-y-4">
        @if (prefill) {
          <p class="text-sm">
            <span class="font-medium">{{ text.enrollment }}:</span> {{ prefill.label }}
          </p>
        } @else {
          <div>
            <label class="field-label" for="pay-search">{{ text.searchStudent }}</label>
            <input
              id="pay-search"
              type="search"
              class="input"
              autocomplete="off"
              [value]="searchText()"
              (input)="onSearch($any($event.target).value)"
              [attr.aria-invalid]="!!errors()['student']"
              [attr.aria-describedby]="
                'pay-search-hint' + (errors()['student'] ? ' pay-search-error' : '')
              "
            />
            <p id="pay-search-hint" class="mt-1.5 text-xs text-muted-foreground">
              {{ text.searchStudentHint }}
            </p>
            @if (errors()['student']; as m) {
              <p id="pay-search-error" class="field-error">{{ m }}</p>
            }
            <div aria-live="polite">
              @if (results() !== null && results()!.length === 0) {
                <p class="mt-2 text-sm text-muted-foreground">{{ text.noStudentFound }}</p>
              }
            </div>
            @if (results()?.length) {
              <ul class="mt-2 space-y-1" [attr.aria-label]="text.student">
                @for (s of results(); track s.id) {
                  <li>
                    <button
                      type="button"
                      class="flex min-h-11 w-full items-center rounded-lg border px-3 text-left text-sm hover:bg-muted/50"
                      [class.border-primary]="studentId() === s.id"
                      [class.border-border]="studentId() !== s.id"
                      [attr.aria-pressed]="studentId() === s.id"
                      (click)="chooseStudent(s)"
                    >
                      {{ s.firstName }} {{ s.lastName }}
                    </button>
                  </li>
                }
              </ul>
            }
          </div>
          @if (studentId()) {
            <div>
              <label class="field-label" for="pay-enrollment">{{ text.enrollment }}</label>
              <select
                id="pay-enrollment"
                class="input"
                (change)="enrollmentId.set($any($event.target).value); clear('enrollment')"
                [attr.aria-invalid]="!!errors()['enrollment']"
                [attr.aria-describedby]="errors()['enrollment'] ? 'pay-enrollment-error' : null"
              >
                <option value="" [selected]="!enrollmentId()">{{ text.chooseEnrollment }}</option>
                @for (e of enrollments(); track e.id) {
                  <option [value]="e.id" [selected]="enrollmentId() === e.id">
                    {{ enrollmentLabel(e) }}
                  </option>
                }
              </select>
              @if (enrollments().length === 0) {
                <p class="mt-1.5 text-xs text-muted-foreground">{{ text.noEnrollments }}</p>
              }
              @if (errors()['enrollment']; as m) {
                <p id="pay-enrollment-error" class="field-error">{{ m }}</p>
              }
            </div>
          }
        }

        <div class="grid gap-4 sm:grid-cols-2">
          <div>
            <label class="field-label" for="pay-period">{{ text.period }}</label>
            <input
              id="pay-period"
              type="month"
              class="input"
              [value]="period()"
              [readOnly]="!!prefill"
              (input)="period.set($any($event.target).value); clear('period')"
              [attr.aria-invalid]="!!errors()['period']"
              [attr.aria-describedby]="
                'pay-period-hint' + (errors()['period'] ? ' pay-period-error' : '')
              "
            />
            <p id="pay-period-hint" class="mt-1.5 text-xs text-muted-foreground">
              {{ text.periodHint }}
            </p>
            @if (errors()['period']; as m) {
              <p id="pay-period-error" class="field-error">{{ m }}</p>
            }
          </div>
          <div>
            <label class="field-label" for="pay-amount">{{ text.amount }}</label>
            <input
              id="pay-amount"
              class="input font-mono"
              inputmode="decimal"
              autocomplete="off"
              [value]="amountText()"
              (input)="amountText.set($any($event.target).value); clear('amount')"
              [attr.aria-invalid]="!!errors()['amount']"
              [attr.aria-describedby]="
                'pay-amount-hint' + (errors()['amount'] ? ' pay-amount-error' : '')
              "
            />
            <p id="pay-amount-hint" class="mt-1.5 text-xs text-muted-foreground">
              {{ amountPreview() ?? text.amountHint }}
            </p>
            @if (errors()['amount']; as m) {
              <p id="pay-amount-error" class="field-error">{{ m }}</p>
            }
          </div>
          <div>
            <label class="field-label" for="pay-method">{{ text.method }}</label>
            <select id="pay-method" class="input" (change)="method.set($any($event.target).value)">
              @for (m of methods; track m) {
                <option [value]="m" [selected]="method() === m">{{ text.methods[m] }}</option>
              }
            </select>
          </div>
          <div>
            <label class="field-label" for="pay-paid-on">{{ text.paidOn }}</label>
            <input
              id="pay-paid-on"
              type="date"
              class="input"
              [max]="today()"
              [value]="paidOn()"
              (input)="paidOn.set($any($event.target).value); clear('paidOn')"
              [attr.aria-invalid]="!!errors()['paidOn']"
              [attr.aria-describedby]="
                'pay-paid-on-hint' + (errors()['paidOn'] ? ' pay-paid-on-error' : '')
              "
            />
            <p id="pay-paid-on-hint" class="mt-1.5 text-xs text-muted-foreground">
              {{ text.paidOnHint }}
            </p>
            @if (errors()['paidOn']; as m) {
              <p id="pay-paid-on-error" class="field-error">{{ m }}</p>
            }
          </div>
        </div>
      </div>

      <div class="mt-6 flex flex-wrap justify-end gap-2">
        <button type="button" class="btn btn-secondary" (click)="ref.close()">
          {{ actions.cancel }}
        </button>
        <button type="submit" class="btn btn-primary" [disabled]="saving()">
          {{ text.recordPayment }}
        </button>
      </div>
    </form>
  `,
})
export class RecordPaymentDialog {
  private readonly api = inject(Api);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly injector = inject(Injector);
  protected readonly ref = inject<DialogRef<PaymentDto>>(DialogRef);
  protected readonly prefill = inject<RecordPaymentData>(DIALOG_DATA)?.prefill ?? null;

  protected readonly dialogClass = dialogClass;
  protected readonly titleId = RECORD_PAYMENT_TITLE_ID;
  protected readonly text = ui.billing;
  protected readonly actions = ui.actions;
  protected readonly methods: PaymentMethod[] = ['Cash', 'BankTransfer'];
  protected readonly today = inject(SchoolClock).today;

  protected readonly searchText = signal('');
  protected readonly results = signal<StudentListItemDto[] | null>(null);
  protected readonly studentId = signal('');
  protected readonly enrollments = signal<EnrollmentDto[]>([]);
  protected readonly enrollmentId = signal(this.prefill?.enrollmentId ?? '');
  protected readonly period = signal(this.prefill?.period ?? this.today().slice(0, 7));
  /** Shown in Turkish format ("1.250,00") but sent as "1250.00". */
  protected readonly amountText = signal(
    this.prefill ? displayMoney(this.prefill.amount).replace('₺', '') : '',
  );
  protected readonly method = signal<PaymentMethod>('Cash');
  protected readonly paidOn = signal(this.today());
  protected readonly saving = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly errors = signal<Partial<Record<Field, string>>>({});
  protected readonly amountPreview = computed(() => {
    const parsed = parseMoneyInput(this.amountText());
    return parsed ? displayMoney(parsed) : null;
  });
  private searchSequence = 0;
  private debounce: ReturnType<typeof setTimeout> | null = null;

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      if (this.debounce) {
        clearTimeout(this.debounce);
      }
    });
  }

  protected enrollmentLabel(e: EnrollmentDto): string {
    return interpolate(this.text.enrollmentOption, {
      group: e.groupName,
      from: formatDate(e.startDate),
      to: e.endDate ? formatDate(e.endDate) : this.text.openEnded,
    });
  }

  protected clear(field: Field): void {
    if (this.errors()[field]) {
      this.errors.update((all) => ({ ...all, [field]: undefined }));
    }
  }

  protected onSearch(value: string): void {
    this.searchText.set(value);
    this.clear('student');
    if (this.debounce) {
      clearTimeout(this.debounce);
    }
    this.debounce = setTimeout(() => {
      this.debounce = null;
      void this.search(value.trim());
    }, PAYMENT_SEARCH_DEBOUNCE_MS);
  }

  protected async chooseStudent(student: StudentListItemDto): Promise<void> {
    this.studentId.set(student.id);
    this.enrollmentId.set('');
    this.enrollments.set([]);
    this.clear('student');
    try {
      const list = await this.api.invoke(apiV1StudentsIdEnrollmentsGet, { id: student.id });
      if (this.studentId() === student.id) {
        this.enrollments.set(list);
        // One enrollment: nothing to choose.
        if (list.length === 1) {
          this.enrollmentId.set(list[0].id);
        }
      }
    } catch (err) {
      this.error.set(problemMessage(toProblem(err)));
    }
  }

  protected async save(): Promise<void> {
    if (this.saving()) {
      return;
    }
    const amount = parseMoneyInput(this.amountText());
    const problems: Partial<Record<Field, string>> = {};
    if (!this.prefill && !this.studentId()) {
      problems.student = errorMessages['Field.Required'];
    } else if (!this.enrollmentId()) {
      problems.enrollment = errorMessages['Field.Required'];
    }
    if (!/^\d{4}-\d{2}$/.test(this.period())) {
      problems.period = errorMessages['YearMonth.Invalid'];
    }
    if (!amount) {
      problems.amount = this.amountText().trim()
        ? errorMessages['Money.Invalid']
        : errorMessages['Field.Required'];
    } else if (!isPositive(amount)) {
      problems.amount = errorMessages['Payment.InvalidAmount'];
    }
    // Same bounds as the API: a real date, 2000–2100, not after the school's today.
    const paidOn = this.paidOn();
    const year = Number(paidOn.slice(0, 4));
    if (
      !/^\d{4}-\d{2}-\d{2}$/.test(paidOn) ||
      year < 2000 ||
      year > 2100 ||
      paidOn > this.today()
    ) {
      problems.paidOn = this.text.paidOnInvalid;
    }
    if (Object.keys(problems).length > 0) {
      this.errors.set(problems);
      this.focusFirst();
      return;
    }
    this.saving.set(true);
    this.error.set(null);
    this.errors.set({});
    try {
      const payment = await this.api.invoke(apiV1PaymentsPost, {
        body: {
          enrollmentId: this.enrollmentId(),
          period: this.period(),
          amount: money(amount!),
          method: this.method(),
          paidOn: this.paidOn(),
        },
      });
      this.ref.close(payment);
    } catch (err) {
      const problem = toProblem(err);
      const fields = fieldErrors(problem);
      const mapped: Partial<Record<Field, string>> = {
        period: fields['period'],
        amount: fields['amount'] ?? fields['amount.amount'],
        paidOn: fields['paidOn'],
        enrollment: fields['enrollmentId'],
      };
      if (problem.code === 'Payment.InvalidAmount' || problem.code === 'Money.Invalid') {
        mapped.amount = problemMessage(problem);
      } else if (
        problem.code === 'Payment.FeeNotDefined' ||
        problem.code === 'Payment.EnrollmentNotActiveInPeriod'
      ) {
        mapped.period = problemMessage(problem);
      }
      // A field error for something this dialog does not show (e.g. the enrollment of a
      // prefilled due) goes to the banner, so no problem is ever invisible.
      const shown = Object.fromEntries(
        Object.entries(mapped).filter(
          ([field, message]) => message && this.isRendered(field as Field),
        ),
      ) as Partial<Record<Field, string>>;
      const hidden = Object.entries(mapped).some(
        ([field, message]) => message && !this.isRendered(field as Field),
      );
      this.errors.set(shown);
      if (hidden || Object.keys(shown).length === 0) {
        this.error.set(problemMessage(problem));
      }
      this.saving.set(false);
      this.focusFirst();
    }
  }

  /** Whether the control for a field is on screen in this mode. */
  private isRendered(field: Field): boolean {
    if (field === 'student') {
      return !this.prefill;
    }
    if (field === 'enrollment') {
      return !this.prefill && !!this.studentId();
    }
    return true;
  }

  private focusFirst(): void {
    afterNextRender(
      () => {
        const errors = this.errors();
        const order: Field[] = ['student', 'enrollment', 'period', 'amount', 'paidOn'];
        const field = order.find((f) => errors[f]);
        const id = field ? fieldIds[field] : this.error() ? 'pay-error' : null;
        if (id) {
          this.host.nativeElement.querySelector<HTMLElement>(`#${id}`)?.focus();
        }
      },
      { injector: this.injector },
    );
  }

  private async search(term: string): Promise<void> {
    const request = ++this.searchSequence;
    if (term.length < 2) {
      this.results.set(null);
      return;
    }
    try {
      const page = await this.api.invoke(apiV1StudentsGet, { search: term, page: 1, pageSize: 10 });
      if (request === this.searchSequence) {
        this.results.set(page.items);
      }
    } catch (err) {
      if (request === this.searchSequence) {
        this.error.set(problemMessage(toProblem(err)));
      }
    }
  }
}

/** What the cancel dialog closes with when the payment was already cancelled. */
export const ALREADY_CANCELLED = 'alreadyCancelled' as const;
export type CancelPaymentResult = PaymentDto | typeof ALREADY_CANCELLED;

export interface CancelPaymentData {
  payment: PaymentDto;
}

/** Cancels a payment with a required reason; the payment is kept, marked cancelled. */
@Component({
  selector: 'app-cancel-payment-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <form [class]="dialogClass" (submit)="$event.preventDefault(); save()" novalidate>
      <h2 [id]="titleId" class="text-lg font-semibold">{{ text.cancelTitle }}</h2>
      <p class="mt-2 text-sm text-muted-foreground">{{ description }}</p>
      @if (error()) {
        <p
          id="cancel-error"
          role="alert"
          tabindex="-1"
          class="mt-3 rounded-lg border border-destructive/30 bg-destructive/10 p-3 text-sm text-destructive"
        >
          {{ error() }}
        </p>
      }
      <div class="mt-4">
        <label class="field-label" for="cancel-reason">{{ text.cancelReason }}</label>
        <textarea
          id="cancel-reason"
          class="input h-auto min-h-24 py-2"
          rows="3"
          maxlength="500"
          [value]="reason()"
          (input)="reason.set($any($event.target).value); reasonError.set(null)"
          [attr.aria-invalid]="!!reasonError()"
          [attr.aria-describedby]="
            'cancel-reason-hint' + (reasonError() ? ' cancel-reason-error' : '')
          "
        ></textarea>
        <p id="cancel-reason-hint" class="mt-1.5 text-xs text-muted-foreground">
          {{ text.cancelReasonHint }}
        </p>
        @if (reasonError(); as m) {
          <p id="cancel-reason-error" class="field-error">{{ m }}</p>
        }
      </div>
      <div class="mt-6 flex flex-wrap justify-end gap-2">
        <button type="button" class="btn btn-secondary" (click)="ref.close()">
          {{ actions.cancel }}
        </button>
        <button type="submit" class="btn btn-destructive" [disabled]="saving()">
          {{ text.cancel }}
        </button>
      </div>
    </form>
  `,
})
export class CancelPaymentDialog {
  private readonly api = inject(Api);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly injector = inject(Injector);
  protected readonly ref = inject<DialogRef<CancelPaymentResult>>(DialogRef);
  private readonly payment = inject<CancelPaymentData>(DIALOG_DATA).payment;

  protected readonly dialogClass = dialogClass;
  protected readonly titleId = CANCEL_PAYMENT_TITLE_ID;
  protected readonly text = ui.billing;
  protected readonly actions = ui.actions;
  protected readonly description = interpolate(ui.billing.cancelText, {
    student: `${this.payment.studentFirstName} ${this.payment.studentLastName}`,
    period: periodLabel(this.payment.period),
    amount: displayMoney(this.payment.amount),
  });
  protected readonly reason = signal('');
  protected readonly reasonError = signal<string | null>(null);
  protected readonly saving = signal(false);
  protected readonly error = signal<string | null>(null);

  protected async save(): Promise<void> {
    if (this.saving()) {
      return;
    }
    const reason = this.reason().trim();
    if (!reason) {
      this.reasonError.set(errorMessages['Payment.CancelReasonRequired']);
      this.focus('cancel-reason');
      return;
    }
    this.saving.set(true);
    this.error.set(null);
    try {
      const cancelled = await this.api.invoke(apiV1PaymentsIdCancelPost, {
        id: this.payment.id,
        body: { reason },
      });
      this.ref.close(cancelled);
    } catch (err) {
      const problem = toProblem(err);
      if (problem.code === 'Payment.AlreadyCancelled') {
        // Someone else cancelled it first: nothing left to do here; the list reloads.
        this.ref.close(ALREADY_CANCELLED);
        return;
      }
      if (problem.code === 'Payment.CancelReasonRequired' || fieldErrors(problem)['reason']) {
        this.reasonError.set(problemMessage(problem));
        this.focus('cancel-reason');
      } else {
        this.error.set(problemMessage(problem));
        this.focus('cancel-error');
      }
      this.saving.set(false);
    }
  }

  private focus(id: string): void {
    afterNextRender(() => this.host.nativeElement.querySelector<HTMLElement>(`#${id}`)?.focus(), {
      injector: this.injector,
    });
  }
}
