import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  inject,
  signal,
} from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';

import { Api } from '../../api/api';
import { apiV1CoachesIdGet, apiV1CoachesIdPut, apiV1CoachesPost } from '../../api/functions';
import { CoachDto } from '../../api/models';
import { problemMessage, toProblem } from '../../core/api/problem';
import { formatPhone } from '../../core/format/format';
import { errorMessages, ui } from '../../core/i18n/tr';
import { focusFirstInvalid } from '../../shared/forms/focus-first-invalid';
import { ServerFieldErrors } from '../../shared/forms/server-field-errors';
import { optionalTurkishPhone } from '../../shared/forms/validators';

export const COACH_FORM_TITLE_ID = 'coach-form-title';

/** Closing value: a created coach carries the one-time password, an edit only says it saved. */
export type CoachFormResult =
  { kind: 'created'; name: string; temporaryPassword: string } | { kind: 'saved' };

type Field = 'firstName' | 'lastName' | 'email' | 'phone';

const ids: Record<Field, string> = {
  firstName: 'c-first',
  lastName: 'c-last',
  email: 'c-email',
  phone: 'c-phone',
};

/** Create (US-04) or edit a coach. Edits send the row version; a conflict reloads the coach. */
@Component({
  selector: 'app-coach-form-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule],
  template: `
    <form
      class="max-h-[calc(100dvh-2rem)] w-[min(32rem,calc(100vw-2rem))] overflow-y-auto rounded-lg border border-border bg-background p-6 shadow-lg"
      [formGroup]="form"
      (ngSubmit)="save()"
      novalidate
    >
      <h2 [id]="titleId" class="text-lg font-semibold">
        {{ coach() ? text.edit : text.add }}
      </h2>
      @if (error()) {
        <div
          role="alert"
          class="mt-3 flex flex-wrap items-center justify-between gap-2 rounded-lg border border-destructive/30 bg-destructive/10 p-3 text-sm text-destructive"
        >
          <span>{{ error() }}</span>
          @if (blocked() === 'reloadFailed') {
            <button type="button" class="btn btn-secondary btn-sm" (click)="retryReload()">
              {{ common.retry }}
            </button>
          }
        </div>
      }
      <div class="mt-4 grid gap-4 sm:grid-cols-2">
        <div>
          <label class="field-label" [for]="ids.firstName">{{ text.firstName }}</label>
          <input
            [id]="ids.firstName"
            class="input"
            formControlName="firstName"
            maxlength="100"
            autocomplete="off"
            [attr.aria-invalid]="!!message('firstName')"
            [attr.aria-describedby]="message('firstName') ? ids.firstName + '-error' : null"
          />
          @if (message('firstName'); as m) {
            <p [id]="ids.firstName + '-error'" class="field-error">{{ m }}</p>
          }
        </div>
        <div>
          <label class="field-label" [for]="ids.lastName">{{ text.lastName }}</label>
          <input
            [id]="ids.lastName"
            class="input"
            formControlName="lastName"
            maxlength="100"
            autocomplete="off"
            [attr.aria-invalid]="!!message('lastName')"
            [attr.aria-describedby]="message('lastName') ? ids.lastName + '-error' : null"
          />
          @if (message('lastName'); as m) {
            <p [id]="ids.lastName + '-error'" class="field-error">{{ m }}</p>
          }
        </div>
        <div class="sm:col-span-2">
          <label class="field-label" [for]="ids.email">{{ text.email }}</label>
          @if (coach(); as current) {
            <input
              [id]="ids.email"
              class="input"
              readonly
              [value]="current.email"
              [attr.aria-describedby]="ids.email + '-hint'"
            />
            <p [id]="ids.email + '-hint'" class="mt-1.5 text-xs text-muted-foreground">
              {{ text.emailReadonly }}
            </p>
          } @else {
            <input
              [id]="ids.email"
              type="email"
              class="input"
              formControlName="email"
              autocomplete="off"
              autocapitalize="none"
              (blur)="trimEmail()"
              [attr.aria-invalid]="!!message('email')"
              [attr.aria-describedby]="message('email') ? ids.email + '-error' : null"
            />
            @if (message('email'); as m) {
              <p [id]="ids.email + '-error'" class="field-error">{{ m }}</p>
            }
          }
        </div>
        <div class="sm:col-span-2">
          <label class="field-label" [for]="ids.phone">{{ text.phone }}</label>
          <input
            [id]="ids.phone"
            type="tel"
            inputmode="tel"
            class="input"
            formControlName="phone"
            maxlength="20"
            autocomplete="off"
            [attr.aria-invalid]="!!message('phone')"
            [attr.aria-describedby]="
              message('phone') ? ids.phone + '-hint ' + ids.phone + '-error' : ids.phone + '-hint'
            "
          />
          <p [id]="ids.phone + '-hint'" class="mt-1.5 text-xs text-muted-foreground">
            {{ text.phoneHint }}
          </p>
          @if (message('phone'); as m) {
            <p [id]="ids.phone + '-error'" class="field-error">{{ m }}</p>
          }
        </div>
      </div>
      <div class="mt-6 flex flex-wrap justify-end gap-2">
        <button type="button" class="btn btn-secondary" (click)="ref.close()">
          {{ actions.cancel }}
        </button>
        <button type="submit" class="btn btn-primary" [disabled]="saving() || blocked() !== null">
          {{ coach() ? actions.save : text.add }}
        </button>
      </div>
    </form>
  `,
})
export class CoachFormDialog {
  private readonly api = inject(Api);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  protected readonly ref = inject<DialogRef<CoachFormResult>>(DialogRef);
  /** The coach being edited (null when creating); replaced with the fresh copy after a conflict. */
  protected readonly coach = signal(inject<CoachDto | null>(DIALOG_DATA));

  protected readonly text = ui.coaches;
  protected readonly actions = ui.actions;
  protected readonly ids = ids;
  protected readonly titleId = COACH_FORM_TITLE_ID;
  protected readonly saving = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly common = ui.common;
  /**
   * Saving is blocked after a conflict when the fresh copy could not be loaded (a retry would
   * only hit 409 again) or when the coach has been deactivated meanwhile.
   */
  protected readonly blocked = signal<'reloadFailed' | 'inactive' | null>(null);
  private readonly server = new ServerFieldErrors<Field>(
    { firstName: 'firstName', lastName: 'lastName', email: 'email', phone: 'phone' },
    { 'User.EmailTaken': 'email' },
  );

  protected readonly form = inject(FormBuilder).nonNullable.group({
    firstName: ['', [Validators.required, Validators.maxLength(100)]],
    lastName: ['', [Validators.required, Validators.maxLength(100)]],
    email: ['', [Validators.required, Validators.email]],
    phone: ['', optionalTurkishPhone],
  });

  constructor() {
    const coach = this.coach();
    if (coach) {
      // The e-mail is the login and cannot be changed here.
      this.form.controls.email.disable();
    }
    this.fill(coach);
    this.server.watch(this.form.controls, inject(DestroyRef));
  }

  /** Pasted addresses often carry spaces; trim before the e-mail rule runs. */
  protected trimEmail(): void {
    const email = this.form.controls.email;
    if (email.enabled && email.value !== email.value.trim()) {
      email.setValue(email.value.trim());
    }
  }

  protected message(field: Field): string | null {
    const server = this.server.get(field);
    if (server) {
      return server;
    }
    const control = this.form.controls[field];
    if (control.disabled || control.valid || !(control.touched || control.dirty)) {
      return null;
    }
    if (control.hasError('required')) {
      return errorMessages['Field.Required'];
    }
    if (control.hasError('maxlength')) {
      return errorMessages['Field.TooLong'];
    }
    if (field === 'email') {
      return errorMessages['Email.Invalid'];
    }
    if (field === 'phone') {
      return errorMessages['PhoneNumber.Invalid'];
    }
    return errorMessages['Field.Invalid'];
  }

  protected async save(): Promise<void> {
    if (this.saving() || this.blocked()) {
      return;
    }
    this.trimEmail();
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      this.focusFirstInvalid();
      return;
    }
    this.saving.set(true);
    this.error.set(null);
    this.server.reset();
    const v = this.form.getRawValue();
    const firstName = v.firstName.trim();
    const lastName = v.lastName.trim();
    const phone = v.phone.trim() === '' ? null : v.phone.trim();
    const coach = this.coach();
    try {
      if (coach) {
        await this.api.invoke(apiV1CoachesIdPut, {
          id: coach.id,
          body: { firstName, lastName, phone, version: Number(coach.version) },
        });
        this.ref.close({ kind: 'saved' });
      } else {
        const created = await this.api.invoke(apiV1CoachesPost, {
          body: { firstName, lastName, email: v.email.trim(), phone },
        });
        this.ref.close({
          kind: 'created',
          name: `${firstName} ${lastName}`,
          temporaryPassword: created.temporaryPassword,
        });
      }
    } catch (err) {
      const problem = toProblem(err);
      if (problem.code === 'Concurrency.Conflict' && coach) {
        await this.reload(coach.id);
      } else {
        this.server.apply(problem);
        this.error.set(problemMessage(problem));
        this.focusFirstInvalid();
      }
      this.saving.set(false);
    }
  }

  protected async retryReload(): Promise<void> {
    const coach = this.coach();
    if (coach && !this.saving()) {
      this.saving.set(true);
      await this.reload(coach.id);
      this.saving.set(false);
    }
  }

  /** Someone else saved first: show their version so the next save carries the fresh version. */
  private async reload(id: string): Promise<void> {
    try {
      const fresh = await this.api.invoke(apiV1CoachesIdGet, { id });
      this.coach.set(fresh);
      this.fill(fresh);
      if (fresh.status !== 'Active') {
        this.block('inactive');
        this.error.set(this.text.inactiveNow);
        return;
      }
      this.blocked.set(null);
      this.form.enable();
      this.form.controls.email.disable();
      this.error.set(ui.common.conflictReloaded);
    } catch {
      this.block('reloadFailed');
      this.error.set(this.text.reloadFailed);
    }
  }

  private block(reason: 'reloadFailed' | 'inactive'): void {
    this.blocked.set(reason);
    this.form.disable();
  }

  private fill(coach: CoachDto | null): void {
    this.form.reset({
      firstName: coach?.firstName ?? '',
      lastName: coach?.lastName ?? '',
      email: coach?.email ?? '',
      phone: coach?.phone ? formatPhone(coach.phone) : '',
    });
  }

  private focusFirstInvalid(): void {
    focusFirstInvalid(
      this.host.nativeElement,
      (Object.keys(ids) as Field[]).map((f) => [ids[f], !!this.message(f)] as const),
    );
  }
}
