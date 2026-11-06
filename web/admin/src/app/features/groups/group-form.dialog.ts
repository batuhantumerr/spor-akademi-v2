import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  computed,
  inject,
  signal,
} from '@angular/core';
import {
  AbstractControl,
  FormBuilder,
  ReactiveFormsModule,
  ValidationErrors,
  Validators,
} from '@angular/forms';

import { Api } from '../../api/api';
import { apiV1GroupsIdGet, apiV1GroupsIdPut, apiV1GroupsPost } from '../../api/functions';
import { GroupDto, NamedReferenceDto } from '../../api/models';
import { problemMessage, toProblem } from '../../core/api/problem';
import { errorMessages, interpolate, ui } from '../../core/i18n/tr';
import { focusFirstInvalid } from '../../shared/forms/focus-first-invalid';
import { ServerFieldErrors } from '../../shared/forms/server-field-errors';
import { GroupLookups } from './group-lookups';

export const GROUP_FORM_TITLE_ID = 'group-form-title';

export interface GroupFormData {
  group: GroupDto | null;
  lookups: GroupLookups;
}

const YEAR_MIN = 1900;
const YEAR_MAX = 2100;
const CAPACITY_MAX = 1000;

type Field = 'sportId' | 'name' | 'birthFrom' | 'birthTo' | 'capacity' | 'facilityId' | 'coachId';

/** In visual order, so the first invalid field gets focus. */
const ids: Record<Field, string> = {
  name: 'g-name',
  sportId: 'g-sport',
  birthFrom: 'g-from',
  birthTo: 'g-to',
  capacity: 'g-capacity',
  facilityId: 'g-facility',
  coachId: 'g-coach',
};

function birthYearOrder(group: AbstractControl): ValidationErrors | null {
  const from = Number(group.get('birthFrom')?.value);
  const to = Number(group.get('birthTo')?.value);
  return Number.isInteger(from) && Number.isInteger(to) && from > to ? { yearOrder: true } : null;
}

/** Create or edit a group (US-08). Closes with true when saved. */
@Component({
  selector: 'app-group-form-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule],
  template: `
    <form
      class="max-h-[calc(100dvh-2rem)] w-[min(36rem,calc(100vw-2rem))] overflow-y-auto rounded-lg border border-border bg-background p-6 shadow-lg"
      [formGroup]="form"
      (ngSubmit)="save()"
      novalidate
    >
      <h2 [id]="titleId" class="text-lg font-semibold">{{ group() ? text.edit : text.add }}</h2>
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
            autocomplete="off"
            [attr.aria-invalid]="!!message('name')"
            [attr.aria-describedby]="message('name') ? ids.name + '-error' : null"
          />
          @if (message('name'); as m) {
            <p [id]="ids.name + '-error'" class="field-error">{{ m }}</p>
          }
        </div>
        <div class="sm:col-span-2">
          <label class="field-label" [for]="ids.sportId">{{ text.sport }}</label>
          @if (group(); as current) {
            <input
              [id]="ids.sportId"
              class="input"
              readonly
              [value]="current.sport.name"
              [attr.aria-describedby]="ids.sportId + '-hint'"
            />
            <p [id]="ids.sportId + '-hint'" class="mt-1.5 text-xs text-muted-foreground">
              {{ text.sportReadonly }}
            </p>
          } @else {
            <select
              [id]="ids.sportId"
              class="input"
              formControlName="sportId"
              [attr.aria-invalid]="!!message('sportId')"
              [attr.aria-describedby]="message('sportId') ? ids.sportId + '-error' : null"
            >
              <option value="" disabled>{{ text.sport }}</option>
              @for (sport of data.lookups.sports; track sport.id) {
                <option [value]="sport.id">{{ sport.name }}</option>
              }
            </select>
            @if (message('sportId'); as m) {
              <p [id]="ids.sportId + '-error'" class="field-error">{{ m }}</p>
            }
          }
        </div>
        <div>
          <label class="field-label" [for]="ids.birthFrom">{{ text.birthYearFrom }}</label>
          <input
            [id]="ids.birthFrom"
            type="number"
            inputmode="numeric"
            class="input"
            formControlName="birthFrom"
            [min]="yearMin"
            [max]="yearMax"
            [attr.aria-invalid]="!!message('birthFrom')"
            [attr.aria-describedby]="
              message('birthFrom') ? 'g-year-hint ' + ids.birthFrom + '-error' : 'g-year-hint'
            "
          />
          @if (message('birthFrom'); as m) {
            <p [id]="ids.birthFrom + '-error'" class="field-error">{{ m }}</p>
          }
        </div>
        <div>
          <label class="field-label" [for]="ids.birthTo">{{ text.birthYearTo }}</label>
          <input
            [id]="ids.birthTo"
            type="number"
            inputmode="numeric"
            class="input"
            formControlName="birthTo"
            [min]="yearMin"
            [max]="yearMax"
            [attr.aria-invalid]="!!message('birthTo')"
            [attr.aria-describedby]="
              message('birthTo') ? 'g-year-hint ' + ids.birthTo + '-error' : 'g-year-hint'
            "
          />
          @if (message('birthTo'); as m) {
            <p [id]="ids.birthTo + '-error'" class="field-error">{{ m }}</p>
          }
        </div>
        <p id="g-year-hint" class="-mt-2 text-xs text-muted-foreground sm:col-span-2">
          {{ text.yearHint }}
        </p>
        <div>
          <label class="field-label" [for]="ids.capacity">{{ text.capacity }}</label>
          <input
            [id]="ids.capacity"
            type="number"
            inputmode="numeric"
            class="input"
            formControlName="capacity"
            min="1"
            [max]="capacityMax"
            [attr.aria-invalid]="!!message('capacity')"
            [attr.aria-describedby]="
              message('capacity')
                ? ids.capacity + '-hint ' + ids.capacity + '-error'
                : ids.capacity + '-hint'
            "
          />
          <p [id]="ids.capacity + '-hint'" class="mt-1.5 text-xs text-muted-foreground">
            {{ text.capacityHint }}
          </p>
          @if (message('capacity'); as m) {
            <p [id]="ids.capacity + '-error'" class="field-error">{{ m }}</p>
          }
        </div>
        <div>
          <label class="field-label" [for]="ids.facilityId">{{ text.facility }}</label>
          <select
            [id]="ids.facilityId"
            class="input"
            formControlName="facilityId"
            [attr.aria-invalid]="!!message('facilityId')"
            [attr.aria-describedby]="message('facilityId') ? ids.facilityId + '-error' : null"
          >
            <option value="" disabled>{{ text.facility }}</option>
            @for (facility of facilities(); track facility.id) {
              <option [value]="facility.id">{{ facility.name }}</option>
            }
          </select>
          @if (message('facilityId'); as m) {
            <p [id]="ids.facilityId + '-error'" class="field-error">{{ m }}</p>
          }
        </div>
        <div class="sm:col-span-2">
          <label class="field-label" [for]="ids.coachId">{{ text.coach }}</label>
          <select
            [id]="ids.coachId"
            class="input"
            formControlName="coachId"
            [attr.aria-invalid]="!!message('coachId')"
            [attr.aria-describedby]="message('coachId') ? ids.coachId + '-error' : null"
          >
            <option value="">{{ text.unassigned }}</option>
            @for (coach of coaches(); track coach.id) {
              <option [value]="coach.id">{{ coach.name }}</option>
            }
          </select>
          @if (message('coachId'); as m) {
            <p [id]="ids.coachId + '-error'" class="field-error">{{ m }}</p>
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
export class GroupFormDialog {
  private readonly api = inject(Api);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  protected readonly ref = inject<DialogRef<boolean>>(DialogRef);
  protected readonly data = inject<GroupFormData>(DIALOG_DATA);
  /** The group being edited (null when creating); replaced with the fresh copy after a conflict. */
  protected readonly group = signal(this.data.group);

  protected readonly text = ui.groups;
  protected readonly actions = ui.actions;
  protected readonly ids = ids;
  protected readonly titleId = GROUP_FORM_TITLE_ID;
  protected readonly yearMin = YEAR_MIN;
  protected readonly yearMax = YEAR_MAX;
  protected readonly capacityMax = CAPACITY_MAX;
  protected readonly saving = signal(false);
  protected readonly error = signal<string | null>(null);

  /** Active facilities, plus the group's current one if it has been archived meanwhile. */
  protected readonly facilities = computed(() =>
    withCurrent(this.data.lookups.facilities, this.group()?.facility, ui.groups.archivedOption),
  );
  /** Active coaches, plus the group's current coach if no longer active. */
  protected readonly coaches = computed(() => {
    const coach = this.group()?.coach;
    return withCurrent(
      this.data.lookups.coaches,
      coach ? { id: coach.id, name: `${coach.firstName} ${coach.lastName}` } : undefined,
      ui.groups.inactiveCoach,
    );
  });

  private readonly server = new ServerFieldErrors<Field>(
    {
      sportId: 'sportId',
      name: 'name',
      birthFrom: 'birthYearRange.from',
      birthTo: 'birthYearRange',
      capacity: 'capacity',
      facilityId: 'facilityId',
      coachId: 'coachId',
    },
    {
      'Group.NameTaken': 'name',
      'Sport.NotFound': 'sportId',
      'Sport.Archived': 'sportId',
      'Facility.NotFound': 'facilityId',
      'Facility.Archived': 'facilityId',
      'Group.CoachNotFound': 'coachId',
      'Schedule.CoachUnavailable': 'coachId',
      'Group.CapacityBelowActiveEnrollments': 'capacity',
      'Group.InvalidCapacity': 'capacity',
      'BirthYearRange.Invalid': 'birthTo',
    },
  );

  protected readonly form = inject(FormBuilder).nonNullable.group(
    {
      sportId: ['', Validators.required],
      name: ['', [Validators.required, Validators.maxLength(100)]],
      birthFrom: [
        null as number | null,
        [Validators.required, Validators.min(YEAR_MIN), Validators.max(YEAR_MAX)],
      ],
      birthTo: [
        null as number | null,
        [Validators.required, Validators.min(YEAR_MIN), Validators.max(YEAR_MAX)],
      ],
      capacity: [
        null as number | null,
        [Validators.required, Validators.min(1), Validators.max(CAPACITY_MAX)],
      ],
      facilityId: ['', Validators.required],
      coachId: [''],
    },
    { validators: birthYearOrder },
  );

  constructor() {
    if (this.group()) {
      this.form.controls.sportId.disable();
    }
    this.fill(this.group());
    this.server.watch(this.form.controls, inject(DestroyRef));
  }

  protected message(field: Field): string | null {
    const server = this.server.get(field);
    if (server) {
      return server;
    }
    const control = this.form.controls[field];
    if (control.disabled || !(control.touched || control.dirty)) {
      return null;
    }
    if (control.hasError('required')) {
      return errorMessages['Field.Required'];
    }
    if (control.hasError('maxlength')) {
      return errorMessages['Field.TooLong'];
    }
    if (field === 'capacity' && control.invalid) {
      return errorMessages['Group.InvalidCapacity'];
    }
    if ((field === 'birthFrom' || field === 'birthTo') && control.invalid) {
      return ui.groups.yearHint;
    }
    if (field === 'birthTo' && this.form.hasError('yearOrder')) {
      return errorMessages['BirthYearRange.Invalid'];
    }
    return control.invalid ? errorMessages['Field.Invalid'] : null;
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
    this.server.reset();
    const v = this.form.getRawValue();
    const fields = {
      name: v.name.trim(),
      birthYearRange: { from: Number(v.birthFrom), to: Number(v.birthTo) },
      capacity: Number(v.capacity),
      facilityId: v.facilityId,
      coachId: v.coachId === '' ? null : v.coachId,
    };
    const group = this.group();
    try {
      if (group) {
        await this.api.invoke(apiV1GroupsIdPut, {
          id: group.id,
          body: { ...fields, version: Number(group.version) },
        });
      } else {
        await this.api.invoke(apiV1GroupsPost, { body: { ...fields, sportId: v.sportId } });
      }
      this.ref.close(true);
    } catch (err) {
      const problem = toProblem(err);
      if (problem.code === 'Concurrency.Conflict' && group) {
        await this.reload(group.id);
      } else {
        this.server.apply(problem);
        this.error.set(problemMessage(problem));
        this.focusFirstInvalid();
      }
      this.saving.set(false);
    }
  }

  /** Someone else saved first: show their version so the next save carries the fresh version. */
  private async reload(id: string): Promise<void> {
    try {
      const fresh = await this.api.invoke(apiV1GroupsIdGet, { id });
      this.group.set(fresh);
      this.fill(fresh);
      this.error.set(ui.common.conflictReloaded);
    } catch (err) {
      this.error.set(problemMessage(toProblem(err)));
    }
  }

  private fill(group: GroupDto | null): void {
    this.form.reset({
      sportId: group?.sport.id ?? '',
      name: group?.name ?? '',
      birthFrom: group ? Number(group.birthYearRange.from) : null,
      birthTo: group ? Number(group.birthYearRange.to) : null,
      capacity: group ? Number(group.capacity) : null,
      facilityId: group?.facility.id ?? '',
      coachId: group?.coach?.id ?? '',
    });
  }

  private focusFirstInvalid(): void {
    focusFirstInvalid(
      this.host.nativeElement,
      (Object.keys(ids) as Field[]).map((f) => [ids[f], !!this.message(f)] as const),
    );
  }
}

/** Options plus the current value when it is no longer among them (archived / inactive). */
function withCurrent(
  options: NamedReferenceDto[],
  current: NamedReferenceDto | undefined,
  template: string,
): NamedReferenceDto[] {
  if (!current || options.some((o) => o.id === current.id)) {
    return options;
  }
  return [{ id: current.id, name: interpolate(template, { name: current.name }) }, ...options];
}
