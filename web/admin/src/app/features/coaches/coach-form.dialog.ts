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
import { ConflictState } from '../../shared/data/conflict-state';
import { ConflictNoticeComponent } from '../../shared/ui/conflict-notice.component';
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

/**
 * Create (US-04) or edit a coach. Edits send the row version; on a conflict the typed values stay
 * and the user chooses between applying them to the fresh version or taking the server's.
 */
@Component({
  selector: 'app-coach-form-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule, ConflictNoticeComponent],
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
        <p
          role="alert"
          class="mt-3 rounded-lg border border-destructive/30 bg-destructive/10 p-3 text-sm text-destructive"
        >
          {{ error() }}
        </p>
      }
      @if (conflictState.open()) {
        <div class="mt-3">
          <app-conflict-notice
            [ready]="conflictState.fresh() !== null"
            [loadError]="conflictState.error()"
            [busy]="saving()"
            (applyMine)="applyMine()"
            (loadServer)="loadServer()"
            (retry)="loadFresh()"
          />
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
        <button
          type="submit"
          class="btn btn-primary"
          [disabled]="saving() || conflictState.open() || inactive()"
        >
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
  /** After a 409 the form keeps the typed values; the server's version loads aside. */
  protected readonly conflictState = new ConflictState<CoachDto>(
    () => this.fetchFresh(),
    () => this.text.reloadFailed,
  );
  /** The coach was deactivated meanwhile: nothing can be saved any more. */
  protected readonly inactive = signal(false);
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
    if (this.saving() || this.conflictState.open() || this.inactive()) {
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
      this.saving.set(false);
      if (problem.code === 'Concurrency.Conflict' && coach) {
        await this.conflictState.start();
        this.blockIfInactive();
        return;
      }
      this.server.apply(problem);
      this.error.set(problemMessage(problem));
      this.focusFirstInvalid();
    }
  }

  /** Re-send the typed values on top of the fresh version. */
  protected async applyMine(): Promise<void> {
    const fresh = this.conflictState.fresh();
    if (!fresh || this.saving()) {
      return;
    }
    this.coach.set(fresh);
    this.conflictState.close();
    await this.save();
  }

  /** Discard the typed values and show the server's version. */
  protected loadServer(): void {
    const fresh = this.conflictState.fresh();
    if (!fresh) {
      return;
    }
    this.coach.set(fresh);
    this.fill(fresh);
    this.conflictState.close();
  }

  /** Retry after a failed load of the server version. */
  protected async loadFresh(): Promise<void> {
    await this.conflictState.load();
    this.blockIfInactive();
  }

  /** A coach deactivated meanwhile cannot be edited: show it and block saving. */
  private blockIfInactive(): void {
    const fresh = this.conflictState.fresh();
    if (fresh && fresh.status !== 'Active') {
      this.conflictState.close();
      this.coach.set(fresh);
      this.inactive.set(true);
      this.form.disable();
      this.error.set(this.text.inactiveNow);
    }
  }

  private fetchFresh(): Promise<CoachDto> {
    return this.api.invoke(apiV1CoachesIdGet, { id: this.coach()!.id });
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
