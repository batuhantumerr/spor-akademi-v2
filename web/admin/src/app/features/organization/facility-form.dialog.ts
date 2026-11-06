import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';

import { Api } from '../../api/api';
import { apiV1FacilitiesIdPut, apiV1FacilitiesPost } from '../../api/functions';
import { FacilityDto, FacilityType } from '../../api/models';
import { fieldErrors, problemMessage, toProblem } from '../../core/api/problem';
import { errorMessages, ui } from '../../core/i18n/tr';

const types: FacilityType[] = ['Field', 'Pool', 'Court', 'Other'];

/** Create or edit a facility (US-07). Closes with true when saved. */
@Component({
  selector: 'app-facility-form-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule],
  template: `
    <form
      class="w-[min(32rem,calc(100vw-2rem))] rounded-lg border border-border bg-background p-6 shadow-lg"
      [formGroup]="form"
      (ngSubmit)="save()"
      novalidate
    >
      <h2 class="text-lg font-semibold">{{ facility ? text.edit : text.add }}</h2>
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
          <label class="field-label" for="f-name">{{ text.name }}</label>
          <input
            id="f-name"
            class="input"
            formControlName="name"
            maxlength="100"
            [attr.aria-invalid]="invalid('name')"
          />
          @if (invalid('name')) {
            <p class="field-error">{{ fieldError('name') }}</p>
          }
        </div>
        <div>
          <label class="field-label" for="f-type">{{ text.type }}</label>
          <select id="f-type" class="input" formControlName="type">
            @for (type of types; track type) {
              <option [value]="type">{{ text.types[type] }}</option>
            }
          </select>
        </div>
        <div>
          <label class="field-label" for="f-capacity">{{ text.capacity }}</label>
          <input
            id="f-capacity"
            type="number"
            min="1"
            max="10000"
            class="input"
            formControlName="capacity"
            [attr.aria-invalid]="invalid('capacity')"
          />
          @if (invalid('capacity')) {
            <p class="field-error">{{ fieldError('capacity') }}</p>
          }
        </div>
        <div class="sm:col-span-2">
          <label class="field-label" for="f-line">{{ text.addressLine }}</label>
          <input
            id="f-line"
            class="input"
            formControlName="line"
            maxlength="200"
            [attr.aria-invalid]="invalid('line')"
          />
          @if (invalid('line')) {
            <p class="field-error">{{ fieldError('line') }}</p>
          }
        </div>
        <div>
          <label class="field-label" for="f-district">{{ text.district }}</label>
          <input
            id="f-district"
            class="input"
            formControlName="district"
            maxlength="60"
            [attr.aria-invalid]="invalid('district')"
          />
          @if (invalid('district')) {
            <p class="field-error">{{ fieldError('district') }}</p>
          }
        </div>
        <div>
          <label class="field-label" for="f-city">{{ text.city }}</label>
          <input
            id="f-city"
            class="input"
            formControlName="city"
            maxlength="60"
            [attr.aria-invalid]="invalid('city')"
          />
          @if (invalid('city')) {
            <p class="field-error">{{ fieldError('city') }}</p>
          }
        </div>
      </div>
      <div class="mt-6 flex justify-end gap-2">
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
  protected readonly ref = inject<DialogRef<boolean>>(DialogRef);
  protected readonly facility = inject<FacilityDto | null>(DIALOG_DATA);

  protected readonly text = ui.facilities;
  protected readonly actions = ui.actions;
  protected readonly types = types;
  protected readonly saving = signal(false);
  protected readonly error = signal<string | null>(null);
  private readonly serverErrors = signal<Record<string, string>>({});

  protected readonly form = inject(FormBuilder).group({
    name: [this.facility?.name ?? '', [Validators.required, Validators.maxLength(100)]],
    type: [(this.facility?.type ?? 'Field') as FacilityType, Validators.required],
    capacity: [
      this.facility?.capacity == null ? null : Number(this.facility.capacity),
      [Validators.min(1), Validators.max(10000)],
    ],
    line: [this.facility?.address.line ?? '', Validators.required],
    district: [this.facility?.address.district ?? '', Validators.required],
    city: [this.facility?.address.city ?? '', Validators.required],
  });

  protected invalid(control: keyof typeof this.form.controls): boolean {
    const c = this.form.controls[control];
    return (c.invalid && (c.touched || c.dirty)) || this.serverError(control) !== undefined;
  }

  protected fieldError(control: keyof typeof this.form.controls): string {
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
    if (this.form.invalid) {
      this.form.markAllAsTouched();
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
    try {
      if (this.facility) {
        await this.api.invoke(apiV1FacilitiesIdPut, {
          id: this.facility.id,
          body: { ...body, version: Number(this.facility.version) },
        });
      } else {
        await this.api.invoke(apiV1FacilitiesPost, { body });
      }
      this.ref.close(true);
    } catch (err) {
      const problem = toProblem(err);
      this.serverErrors.set(fieldErrors(problem));
      this.error.set(problemMessage(problem));
      this.saving.set(false);
    }
  }

  private serverError(control: string): string | undefined {
    const errors = this.serverErrors();
    const key = ['line', 'district', 'city'].includes(control) ? `address.${control}` : control;
    return errors[key];
  }
}
