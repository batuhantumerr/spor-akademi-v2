import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  inject,
  signal,
} from '@angular/core';
import { AbstractControl, FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';

import { Api } from '../../api/api';
import {
  apiV1FacilitiesIdGet,
  apiV1FacilitiesIdPut,
  apiV1FacilitiesPost,
} from '../../api/functions';
import { FacilityDto, FacilityType } from '../../api/models';
import { fieldErrors, problemMessage, toProblem } from '../../core/api/problem';
import { errorMessages, ui } from '../../core/i18n/tr';
import { focusFirstInvalid } from '../../shared/forms/focus-first-invalid';

const types: FacilityType[] = ['Field', 'Pool', 'Court', 'Other'];

/** Id of the dialog title; pass it as `ariaLabelledBy` when opening the dialog. */
export const FACILITY_FORM_TITLE_ID = 'facility-form-title';

type Field = 'name' | 'type' | 'capacity' | 'line' | 'district' | 'city';

/** API field paths for the form controls (the address is nested). */
const serverKeys: Record<Field, string> = {
  name: 'name',
  type: 'type',
  capacity: 'capacity',
  line: 'address.line',
  district: 'address.district',
  city: 'address.city',
};

const fieldIds: Record<Field, string> = {
  name: 'f-name',
  type: 'f-type',
  capacity: 'f-capacity',
  line: 'f-line',
  district: 'f-district',
  city: 'f-city',
};

/** Create or edit a facility (US-07). Closes with true when saved. */
@Component({
  selector: 'app-facility-form-dialog',
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
        {{ facility() ? text.edit : text.add }}
      </h2>
      @if (error()) {
        <p
          role="alert"
          class="mt-3 rounded-lg border border-destructive/30 bg-destructive/10 p-3 text-sm text-destructive"
        >
          {{ error() }}
        </p>
      }
      <div class="mt-4 grid gap-4 sm:grid-cols-2">
        <div class="sm:col-span-2">
          <label class="field-label" [for]="ids.name">{{ text.name }}</label>
          <input
            [id]="ids.name"
            class="input"
            formControlName="name"
            maxlength="100"
            [attr.aria-invalid]="invalid('name')"
            [attr.aria-describedby]="invalid('name') ? ids.name + '-error' : null"
          />
          @if (invalid('name')) {
            <p [id]="ids.name + '-error'" class="field-error">{{ fieldError('name') }}</p>
          }
        </div>
        <div>
          <label class="field-label" [for]="ids.type">{{ text.type }}</label>
          <select
            [id]="ids.type"
            class="input"
            formControlName="type"
            [attr.aria-invalid]="invalid('type')"
            [attr.aria-describedby]="invalid('type') ? ids.type + '-error' : null"
          >
            @for (type of types; track type) {
              <option [value]="type">{{ text.types[type] }}</option>
            }
          </select>
          @if (invalid('type')) {
            <p [id]="ids.type + '-error'" class="field-error">{{ fieldError('type') }}</p>
          }
        </div>
        <div>
          <label class="field-label" [for]="ids.capacity">{{ text.capacity }}</label>
          <input
            [id]="ids.capacity"
            type="number"
            inputmode="numeric"
            min="1"
            max="10000"
            class="input"
            formControlName="capacity"
            [attr.aria-invalid]="invalid('capacity')"
            [attr.aria-describedby]="invalid('capacity') ? ids.capacity + '-error' : null"
          />
          @if (invalid('capacity')) {
            <p [id]="ids.capacity + '-error'" class="field-error">{{ fieldError('capacity') }}</p>
          }
        </div>
        <div class="sm:col-span-2">
          <label class="field-label" [for]="ids.line">{{ text.addressLine }}</label>
          <input
            [id]="ids.line"
            class="input"
            formControlName="line"
            maxlength="200"
            [attr.aria-invalid]="invalid('line')"
            [attr.aria-describedby]="invalid('line') ? ids.line + '-error' : null"
          />
          @if (invalid('line')) {
            <p [id]="ids.line + '-error'" class="field-error">{{ fieldError('line') }}</p>
          }
        </div>
        <div>
          <label class="field-label" [for]="ids.district">{{ text.district }}</label>
          <input
            [id]="ids.district"
            class="input"
            formControlName="district"
            maxlength="60"
            [attr.aria-invalid]="invalid('district')"
            [attr.aria-describedby]="invalid('district') ? ids.district + '-error' : null"
          />
          @if (invalid('district')) {
            <p [id]="ids.district + '-error'" class="field-error">{{ fieldError('district') }}</p>
          }
        </div>
        <div>
          <label class="field-label" [for]="ids.city">{{ text.city }}</label>
          <input
            [id]="ids.city"
            class="input"
            formControlName="city"
            maxlength="60"
            [attr.aria-invalid]="invalid('city')"
            [attr.aria-describedby]="invalid('city') ? ids.city + '-error' : null"
          />
          @if (invalid('city')) {
            <p [id]="ids.city + '-error'" class="field-error">{{ fieldError('city') }}</p>
          }
        </div>
      </div>
      <div class="mt-6 flex flex-wrap justify-end gap-2">
        <button type="button" class="btn btn-secondary" (click)="ref.close(false)">
          {{ actions.cancel }}
        </button>
        <button type="submit" class="btn btn-primary" [disabled]="saving()">
          {{ actions.save }}
        </button>
      </div>
    </form>
  `,
})
export class FacilityFormDialog {
  private readonly api = inject(Api);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  protected readonly ref = inject<DialogRef<boolean>>(DialogRef);
  /** The facility being edited (null when creating); replaced with the fresh copy after a conflict. */
  protected readonly facility = signal(inject<FacilityDto | null>(DIALOG_DATA));

  protected readonly text = ui.facilities;
  protected readonly actions = ui.actions;
  protected readonly types = types;
  protected readonly ids = fieldIds;
  protected readonly titleId = FACILITY_FORM_TITLE_ID;
  protected readonly saving = signal(false);
  protected readonly error = signal<string | null>(null);
  private readonly serverErrors = signal<Record<string, string>>({});

  protected readonly form = inject(FormBuilder).group({
    name: ['', [Validators.required, Validators.maxLength(100)]],
    type: ['Field' as FacilityType, Validators.required],
    capacity: [null as number | null, [Validators.min(1), Validators.max(10000)]],
    line: ['', Validators.required],
    district: ['', Validators.required],
    city: ['', Validators.required],
  });

  constructor() {
    this.fill(this.facility());
    // A server error belongs to the value it was reported for; editing the field clears it.
    const destroyRef = inject(DestroyRef);
    for (const field of Object.keys(serverKeys) as Field[]) {
      const control: AbstractControl = this.form.controls[field];
      const subscription = control.valueChanges.subscribe(() => {
        const key = serverKeys[field];
        if (this.serverErrors()[key]) {
          this.serverErrors.update((errors) => {
            const rest = { ...errors };
            delete rest[key];
            return rest;
          });
        }
      });
      destroyRef.onDestroy(() => subscription.unsubscribe());
    }
  }

  protected invalid(control: Field): boolean {
    const c = this.form.controls[control];
    return (c.invalid && (c.touched || c.dirty)) || this.serverError(control) !== undefined;
  }

  protected fieldError(control: Field): string {
    const server = this.serverError(control);
    if (server) {
      return server;
    }
    const c = this.form.controls[control];
    return c.hasError('required')
      ? errorMessages['Field.Required']
      : errorMessages['Field.Invalid'];
  }

  protected async save(): Promise<void> {
    if (this.saving()) {
      return;
    }
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      this.focusFirstInvalid();
      return;
    }
    this.saving.set(true);
    this.error.set(null);
    this.serverErrors.set({});
    const v = this.form.getRawValue();
    const body = {
      name: v.name ?? '',
      type: v.type ?? 'Field',
      capacity: v.capacity === null || (v.capacity as unknown) === '' ? null : Number(v.capacity),
      address: { line: v.line ?? '', district: v.district ?? '', city: v.city ?? '' },
    };
    const facility = this.facility();
    try {
      if (facility) {
        await this.api.invoke(apiV1FacilitiesIdPut, {
          id: facility.id,
          body: { ...body, version: Number(facility.version) },
        });
      } else {
        await this.api.invoke(apiV1FacilitiesPost, { body });
      }
      this.ref.close(true);
    } catch (err) {
      const problem = toProblem(err);
      if (problem.code === 'Concurrency.Conflict' && facility) {
        await this.reload(facility.id);
      } else {
        this.serverErrors.set(fieldErrors(problem));
        this.error.set(problemMessage(problem));
        this.focusFirstInvalid();
      }
      this.saving.set(false);
    }
  }

  /** Someone else saved first: show their version so the next save carries the fresh version. */
  private async reload(id: string): Promise<void> {
    try {
      const fresh = await this.api.invoke(apiV1FacilitiesIdGet, { id });
      this.facility.set(fresh);
      this.fill(fresh);
      this.error.set(ui.common.conflictReloaded);
    } catch (err) {
      this.error.set(problemMessage(toProblem(err)));
    }
  }

  private fill(facility: FacilityDto | null): void {
    this.form.reset({
      name: facility?.name ?? '',
      type: facility?.type ?? 'Field',
      capacity: facility?.capacity == null ? null : Number(facility.capacity),
      line: facility?.address.line ?? '',
      district: facility?.address.district ?? '',
      city: facility?.address.city ?? '',
    });
  }

  private focusFirstInvalid(): void {
    focusFirstInvalid(
      this.host.nativeElement,
      (Object.keys(fieldIds) as Field[]).map((f) => [fieldIds[f], this.invalid(f)] as const),
    );
  }

  private serverError(control: Field): string | undefined {
    return this.serverErrors()[serverKeys[control]];
  }
}
