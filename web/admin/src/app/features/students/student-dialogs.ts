import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  inject,
  signal,
} from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';

import { Api } from '../../api/api';
import {
  apiV1GuardiansGet,
  apiV1GuardiansIdPut,
  apiV1StudentsIdGet,
  apiV1StudentsIdLeavePost,
  apiV1StudentsIdPut,
} from '../../api/functions';
import { GuardianDto, StudentDto } from '../../api/models';
import { problemMessage, toProblem } from '../../core/api/problem';
import { formatPhone } from '../../core/format/format';
import { ui } from '../../core/i18n/tr';
import { todayIso } from '../../core/students/age';
import { optionalTurkishPhone } from '../../shared/forms/validators';
import { PathErrors, clientMessage } from './path-errors';
import {
  EmergencyFieldsComponent,
  StudentProfileFieldsComponent,
} from './student-fields.component';
import {
  StudentField,
  studentApiPaths,
  studentBody,
  studentCodePaths,
  studentFieldsForm,
} from './student-fields';

export const STUDENT_EDIT_TITLE_ID = 'student-edit-title';
export const GUARDIAN_EDIT_TITLE_ID = 'guardian-edit-title';
export const LEAVE_TITLE_ID = 'student-leave-title';

const dialogClass =
  'max-h-[calc(100dvh-2rem)] w-[min(44rem,calc(100vw-2rem))] overflow-y-auto rounded-lg border border-border bg-background p-4 shadow-lg sm:p-6';

function focusFirstInvalid(host: HTMLElement): void {
  host.querySelector<HTMLElement>('[aria-invalid="true"]')?.focus();
}

/** Edit a student's own data and emergency contact; sends the version, reloads on a conflict. */
@Component({
  selector: 'app-student-edit-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [StudentProfileFieldsComponent, EmergencyFieldsComponent],
  template: `
    <form [class]="dialogClass" (submit)="$event.preventDefault(); save()" novalidate>
      <h2 [id]="titleId" class="mb-3 text-lg font-semibold">{{ text.editTitle }}</h2>
      @if (error()) {
        <p
          role="alert"
          class="mb-3 rounded-lg border border-destructive/30 bg-destructive/10 p-3 text-sm text-destructive"
        >
          {{ error() }}
        </p>
      }
      <div class="space-y-4">
        <app-student-profile-fields
          prefix="e-"
          [form]="form"
          [serverError]="serverError"
          [submitted]="submitted()"
        />
        <app-emergency-fields
          prefix="e-"
          [form]="form"
          [serverError]="serverError"
          [submitted]="submitted()"
        />
      </div>
      <div class="mt-6 flex flex-wrap justify-end gap-2">
        <button type="button" class="btn btn-secondary" (click)="ref.close()">
          {{ actions.cancel }}
        </button>
        <button type="submit" class="btn btn-primary" [disabled]="saving()">
          {{ actions.save }}
        </button>
      </div>
    </form>
  `,
})
export class StudentEditDialog {
  private readonly api = inject(Api);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  protected readonly ref = inject<DialogRef<StudentDto>>(DialogRef);
  private student = inject<StudentDto>(DIALOG_DATA);

  protected readonly dialogClass = dialogClass;
  protected readonly titleId = STUDENT_EDIT_TITLE_ID;
  protected readonly text = ui.studentDetail;
  protected readonly actions = ui.actions;
  protected form = studentFieldsForm(this.student);
  protected readonly saving = signal(false);
  protected readonly submitted = signal(false);
  protected readonly error = signal<string | null>(null);
  private readonly errors = new PathErrors(studentCodePaths);
  protected readonly serverError = (path: string) => this.errors.get(path);

  constructor() {
    const destroyRef = inject(DestroyRef);
    for (const field of Object.keys(studentApiPaths) as StudentField[]) {
      const sub = this.form.controls[field].valueChanges.subscribe(() =>
        this.errors.clear(studentApiPaths[field]),
      );
      destroyRef.onDestroy(() => sub.unsubscribe());
    }
  }

  protected async save(): Promise<void> {
    if (this.saving()) {
      return;
    }
    this.submitted.set(true);
    this.form.markAllAsTouched();
    if (this.form.invalid) {
      setTimeout(() => focusFirstInvalid(this.host.nativeElement));
      return;
    }
    this.saving.set(true);
    this.error.set(null);
    this.errors.reset();
    try {
      const saved = await this.api.invoke(apiV1StudentsIdPut, {
        id: this.student.id,
        body: { ...studentBody(this.form), version: Number(this.student.version) },
      });
      this.ref.close(saved);
    } catch (err) {
      const problem = toProblem(err);
      if (problem.code === 'Concurrency.Conflict') {
        await this.reload();
      } else {
        this.errors.apply(problem);
        this.error.set(problemMessage(problem));
        setTimeout(() => focusFirstInvalid(this.host.nativeElement));
      }
      this.saving.set(false);
    }
  }

  /** Someone else saved first: show their data so the next save carries the fresh version. */
  private async reload(): Promise<void> {
    try {
      const fresh = await this.api.invoke(apiV1StudentsIdGet, { id: this.student.id });
      this.student = fresh;
      this.form.reset(studentFieldsForm(fresh).getRawValue());
      this.error.set(ui.common.conflictReloaded);
    } catch (err) {
      this.error.set(problemMessage(toProblem(err)));
    }
  }
}

export interface GuardianEditData {
  /** The guardian's current phone; the dialog loads the full record (with version) by it. */
  phone: string;
}

/**
 * Edit a guardian's contact data. The student view has no version for the guardian, so the
 * dialog loads the guardian by phone first and sends that version.
 */
@Component({
  selector: 'app-guardian-edit-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule],
  template: `
    <form
      [class]="dialogClass"
      [formGroup]="form"
      (submit)="$event.preventDefault(); save()"
      novalidate
    >
      <h2 [id]="titleId" class="text-lg font-semibold">{{ text.editGuardianTitle }}</h2>
      <p class="mt-1 text-xs text-muted-foreground">{{ text.guardianShared }}</p>
      @if (error()) {
        <p
          role="alert"
          class="mt-3 rounded-lg border border-destructive/30 bg-destructive/10 p-3 text-sm text-destructive"
        >
          {{ error() }}
        </p>
      }
      @if (!guardian() && !error()) {
        <p role="status" class="mt-3 text-sm text-muted-foreground">{{ common.loading }}</p>
      }
      @if (guardian()) {
        <div class="mt-4 grid gap-4 sm:grid-cols-2">
          @for (field of fields; track field.control) {
            <div [class.sm:col-span-2]="field.wide">
              <label class="field-label" [for]="'ge-' + field.control">{{
                labels[field.label]
              }}</label>
              <input
                [id]="'ge-' + field.control"
                class="input"
                [type]="field.type"
                [formControlName]="field.control"
                autocomplete="off"
                [attr.aria-invalid]="!!message(field.control)"
                [attr.aria-describedby]="
                  message(field.control) ? 'ge-' + field.control + '-error' : null
                "
              />
              @if (message(field.control); as m) {
                <p [id]="'ge-' + field.control + '-error'" class="field-error">{{ m }}</p>
              }
            </div>
          }
        </div>
      }
      <div class="mt-6 flex flex-wrap justify-end gap-2">
        <button type="button" class="btn btn-secondary" (click)="ref.close()">
          {{ actions.cancel }}
        </button>
        <button type="submit" class="btn btn-primary" [disabled]="saving() || !guardian()">
          {{ actions.save }}
        </button>
      </div>
    </form>
  `,
})
export class GuardianEditDialog {
  private readonly api = inject(Api);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  protected readonly ref = inject<DialogRef<GuardianDto>>(DialogRef);
  private readonly data = inject<GuardianEditData>(DIALOG_DATA);

  protected readonly dialogClass = dialogClass;
  protected readonly titleId = GUARDIAN_EDIT_TITLE_ID;
  protected readonly text = ui.studentDetail;
  protected readonly labels = ui.studentForm;
  protected readonly actions = ui.actions;
  protected readonly common = ui.common;
  protected readonly guardian = signal<GuardianDto | null>(null);
  protected readonly saving = signal(false);
  protected readonly submitted = signal(false);
  protected readonly error = signal<string | null>(null);
  private readonly errors = new PathErrors({ 'Guardian.PhoneTaken': 'phone' });

  protected readonly fields = [
    { control: 'firstName', label: 'firstName', type: 'text', wide: false },
    { control: 'lastName', label: 'lastName', type: 'text', wide: false },
    { control: 'phone', label: 'phone', type: 'tel', wide: false },
    { control: 'email', label: 'email', type: 'email', wide: false },
    { control: 'line', label: 'addressLine', type: 'text', wide: true },
    { control: 'district', label: 'district', type: 'text', wide: false },
    { control: 'city', label: 'city', type: 'text', wide: false },
  ] as const;

  private readonly apiPaths: Record<string, string> = {
    firstName: 'firstName',
    lastName: 'lastName',
    phone: 'phone',
    email: 'email',
    line: 'address.line',
    district: 'address.district',
    city: 'address.city',
  };

  protected readonly form = new FormGroup({
    firstName: new FormControl('', {
      nonNullable: true,
      validators: [Validators.required, Validators.maxLength(100)],
    }),
    lastName: new FormControl('', {
      nonNullable: true,
      validators: [Validators.required, Validators.maxLength(100)],
    }),
    phone: new FormControl('', {
      nonNullable: true,
      validators: [Validators.required, optionalTurkishPhone],
    }),
    email: new FormControl('', { nonNullable: true, validators: [Validators.email] }),
    line: new FormControl('', { nonNullable: true, validators: [Validators.maxLength(200)] }),
    district: new FormControl('', { nonNullable: true, validators: [Validators.maxLength(60)] }),
    city: new FormControl('', { nonNullable: true, validators: [Validators.maxLength(60)] }),
  });

  constructor() {
    void this.load(this.data.phone);
    const destroyRef = inject(DestroyRef);
    for (const [control, path] of Object.entries(this.apiPaths)) {
      const sub = this.form.get(control)!.valueChanges.subscribe(() => {
        this.errors.clear(path);
        if (path.startsWith('address')) {
          this.errors.clear('address');
        }
      });
      destroyRef.onDestroy(() => sub.unsubscribe());
    }
  }

  protected message(control: string): string | null {
    const server =
      this.errors.get(this.apiPaths[control]) ??
      (control === 'line' ? this.errors.get('address') : undefined);
    if (server) {
      return server;
    }
    const c = this.form.get(control)!;
    if (control === 'line' || control === 'district' || control === 'city') {
      // The address is optional, but all three parts go together.
      const v = this.form.getRawValue();
      const any = [v.line, v.district, v.city].some((x) => x.trim() !== '');
      if (any && c.value.trim() === '' && (c.touched || this.submitted())) {
        return clientMessage(new FormControl('', Validators.required));
      }
    }
    return c.touched || c.dirty || this.submitted() ? clientMessage(c) : null;
  }

  protected async save(): Promise<void> {
    const guardian = this.guardian();
    if (!guardian || this.saving()) {
      return;
    }
    this.submitted.set(true);
    this.form.markAllAsTouched();
    if (Object.keys(this.apiPaths).some((c) => this.message(c))) {
      setTimeout(() => focusFirstInvalid(this.host.nativeElement));
      return;
    }
    this.saving.set(true);
    this.error.set(null);
    this.errors.reset();
    const v = this.form.getRawValue();
    const hasAddress = [v.line, v.district, v.city].some((x) => x.trim() !== '');
    try {
      const saved = await this.api.invoke(apiV1GuardiansIdPut, {
        id: guardian.id,
        body: {
          firstName: v.firstName.trim(),
          lastName: v.lastName.trim(),
          phone: v.phone.trim(),
          email: v.email.trim() === '' ? null : v.email.trim(),
          address: hasAddress
            ? { line: v.line.trim(), district: v.district.trim(), city: v.city.trim() }
            : null,
          version: Number(guardian.version),
        },
      });
      this.ref.close(saved);
    } catch (err) {
      const problem = toProblem(err);
      if (problem.code === 'Concurrency.Conflict') {
        await this.load(guardian.phone);
        if (this.guardian()) {
          this.error.set(ui.common.conflictReloaded);
        }
      } else {
        this.errors.apply(problem);
        this.error.set(problemMessage(problem));
        setTimeout(() => focusFirstInvalid(this.host.nativeElement));
      }
      this.saving.set(false);
    }
  }

  private async load(phone: string): Promise<void> {
    try {
      const result = await this.api.invoke(apiV1GuardiansGet, { phone });
      const guardian = result.items[0] ?? null;
      if (!guardian) {
        this.guardian.set(null);
        this.error.set(this.text.guardianGone);
        return;
      }
      this.guardian.set(guardian);
      this.form.reset({
        firstName: guardian.firstName,
        lastName: guardian.lastName,
        phone: formatPhone(guardian.phone),
        email: guardian.email ?? '',
        line: guardian.address?.line ?? '',
        district: guardian.address?.district ?? '',
        city: guardian.address?.city ?? '',
      });
    } catch (err) {
      this.error.set(problemMessage(toProblem(err)));
    }
  }
}

/** Marks a student as left on a date (today or earlier); closes with the updated student. */
@Component({
  selector: 'app-student-leave-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule],
  template: `
    <form
      class="w-[min(28rem,calc(100vw-2rem))] rounded-lg border border-border bg-background p-6 shadow-lg"
      (submit)="$event.preventDefault(); save()"
      novalidate
    >
      <h2 [id]="titleId" class="text-lg font-semibold">{{ text.leaveTitle }}</h2>
      <p id="leave-text" class="mt-2 text-sm text-muted-foreground">{{ text.leaveText }}</p>
      @if (error()) {
        <p
          role="alert"
          class="mt-3 rounded-lg border border-destructive/30 bg-destructive/10 p-3 text-sm text-destructive"
        >
          {{ error() }}
        </p>
      }
      <div class="mt-4">
        <label class="field-label" for="leave-date">{{ text.leaveDate }}</label>
        <input
          id="leave-date"
          type="date"
          class="input"
          [formControl]="date"
          [max]="today"
          [attr.aria-invalid]="!!dateError()"
          [attr.aria-describedby]="
            dateError() ? 'leave-date-hint leave-date-error' : 'leave-date-hint'
          "
        />
        <p id="leave-date-hint" class="mt-1.5 text-xs text-muted-foreground">
          {{ text.leaveDateHint }}
        </p>
        @if (dateError(); as m) {
          <p id="leave-date-error" class="field-error">{{ m }}</p>
        }
      </div>
      <div class="mt-6 flex flex-wrap justify-end gap-2">
        <button type="button" class="btn btn-secondary" (click)="ref.close()">
          {{ actions.cancel }}
        </button>
        <button type="submit" class="btn btn-destructive" [disabled]="saving()">
          {{ text.leave }}
        </button>
      </div>
    </form>
  `,
})
export class StudentLeaveDialog {
  private readonly api = inject(Api);
  protected readonly ref = inject<DialogRef<StudentDto>>(DialogRef);
  private readonly student = inject<StudentDto>(DIALOG_DATA);

  protected readonly titleId = LEAVE_TITLE_ID;
  protected readonly text = ui.studentDetail;
  protected readonly actions = ui.actions;
  protected readonly today = todayIso();
  protected readonly date = new FormControl(this.today, {
    nonNullable: true,
    validators: [Validators.required],
  });
  protected readonly saving = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly serverDateError = signal<string | null>(null);
  private readonly submitted = signal(false);

  constructor() {
    const sub = this.date.valueChanges.subscribe(() => this.serverDateError.set(null));
    inject(DestroyRef).onDestroy(() => sub.unsubscribe());
  }

  protected dateError(): string | null {
    if (this.serverDateError()) {
      return this.serverDateError();
    }
    const value = this.date.value;
    if (!this.submitted() && !this.date.dirty) {
      return null;
    }
    if (!value) {
      return clientMessage(this.date);
    }
    return value > this.today || value < this.student.birthDate ? this.text.leaveDateHint : null;
  }

  protected async save(): Promise<void> {
    if (this.saving()) {
      return;
    }
    this.submitted.set(true);
    if (this.dateError()) {
      return;
    }
    this.saving.set(true);
    this.error.set(null);
    try {
      const updated = await this.api.invoke(apiV1StudentsIdLeavePost, {
        id: this.student.id,
        body: { date: this.date.value },
      });
      this.ref.close(updated);
    } catch (err) {
      const problem = toProblem(err);
      const message = problemMessage(problem);
      if (problem.errors?.['date'] || problem.code === 'Student.InvalidLeaveDate') {
        this.serverDateError.set(
          problem.code === 'Student.InvalidLeaveDate' ? message : this.text.leaveDateHint,
        );
      } else {
        this.error.set(message);
      }
      this.saving.set(false);
    }
  }
}
