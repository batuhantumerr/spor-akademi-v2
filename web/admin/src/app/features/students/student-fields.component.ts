import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  input,
  output,
  signal,
} from '@angular/core';
import { ReactiveFormsModule } from '@angular/forms';
import { LucideCopy } from '@lucide/angular';

import { interpolate, ui } from '../../core/i18n/tr';
import { todayIso } from '../../core/students/age';
import { clientMessage } from './path-errors';
import { StudentField, StudentFieldsForm, ageInfo, studentApiPaths } from './student-fields';

/** Message for a student control: the API's error first, then the client rule once used. */
function fieldMessage(
  form: StudentFieldsForm,
  serverError: (path: string) => string | undefined,
  submitted: boolean,
  field: StudentField,
): string | null {
  const server = serverError(studentApiPaths[field]);
  if (server) {
    return server;
  }
  const control = form.controls[field];
  return control.touched || control.dirty || submitted ? clientMessage(control) : null;
}

/** Student section (names, birth date with a minor/adult hint, gender) and own contact. */
@Component({
  selector: 'app-student-profile-fields',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule],
  template: `
    @let f = form();
    <fieldset class="card p-4 sm:p-6" [formGroup]="f">
      <legend class="sr-only">{{ text.studentSection }}</legend>
      <h2 class="mb-4 text-base font-semibold" aria-hidden="true">{{ text.studentSection }}</h2>
      <div class="grid gap-4 sm:grid-cols-2">
        @for (field of nameFields; track field) {
          <div>
            <label class="field-label" [for]="prefix() + field">
              {{ text[field] }} <span class="text-destructive" aria-hidden="true">*</span>
            </label>
            <input
              [id]="prefix() + field"
              class="input"
              [formControlName]="field"
              maxlength="100"
              autocomplete="off"
              aria-required="true"
              [attr.aria-invalid]="!!message(field)"
              [attr.aria-describedby]="message(field) ? prefix() + field + '-error' : null"
            />
            @if (message(field); as m) {
              <p [id]="prefix() + field + '-error'" class="field-error">{{ m }}</p>
            }
          </div>
        }
        <div>
          <label class="field-label" [for]="prefix() + 'birthDate'">
            {{ text.birthDate }} <span class="text-destructive" aria-hidden="true">*</span>
          </label>
          <input
            [id]="prefix() + 'birthDate'"
            type="date"
            class="input"
            formControlName="birthDate"
            [max]="today"
            aria-required="true"
            [attr.aria-invalid]="!!message('birthDate')"
            [attr.aria-describedby]="
              (message('birthDate') ? prefix() + 'birthDate-error ' : '') + prefix() + 'age-hint'
            "
          />
          @if (message('birthDate'); as m) {
            <p [id]="prefix() + 'birthDate-error'" class="field-error">{{ m }}</p>
          }
          <p
            [id]="prefix() + 'age-hint'"
            class="mt-1.5 text-xs text-muted-foreground"
            aria-live="polite"
          >
            @if (age(); as a) {
              <span [class.text-warning]="a.minor">{{ a.hint }}</span>
            }
          </p>
        </div>
        <div>
          <label class="field-label" [for]="prefix() + 'gender'">
            {{ text.gender }} <span class="text-destructive" aria-hidden="true">*</span>
          </label>
          <select
            [id]="prefix() + 'gender'"
            class="input"
            formControlName="gender"
            aria-required="true"
            [attr.aria-invalid]="!!message('gender')"
            [attr.aria-describedby]="message('gender') ? prefix() + 'gender-error' : null"
          >
            <option value="" disabled>{{ text.chooseGender }}</option>
            <option value="Female">{{ genders['Female'] }}</option>
            <option value="Male">{{ genders['Male'] }}</option>
          </select>
          @if (message('gender'); as m) {
            <p [id]="prefix() + 'gender-error'" class="field-error">{{ m }}</p>
          }
        </div>
      </div>

      <h3 class="mt-6 mb-3 text-sm font-semibold">{{ text.contactSection }}</h3>
      <div class="grid gap-4 sm:grid-cols-2">
        <div>
          <label class="field-label" [for]="prefix() + 'phone'">
            {{ adult() ? text.phone : text.phoneOptional }}
            @if (adult()) {
              <span class="text-destructive" aria-hidden="true">*</span>
            }
          </label>
          <input
            [id]="prefix() + 'phone'"
            type="tel"
            inputmode="tel"
            class="input"
            formControlName="phone"
            autocomplete="off"
            [attr.aria-required]="adult()"
            [attr.aria-invalid]="!!message('phone')"
            [attr.aria-describedby]="
              prefix() + 'phone-hint' + (message('phone') ? ' ' + prefix() + 'phone-error' : '')
            "
          />
          <p [id]="prefix() + 'phone-hint'" class="mt-1.5 text-xs text-muted-foreground">
            {{ text.phoneHint }}
          </p>
          @if (message('phone'); as m) {
            <p [id]="prefix() + 'phone-error'" class="field-error">{{ m }}</p>
          }
        </div>
        <div>
          <label class="field-label" [for]="prefix() + 'email'">{{ text.email }}</label>
          <input
            [id]="prefix() + 'email'"
            type="email"
            class="input"
            formControlName="email"
            autocomplete="off"
            autocapitalize="none"
            [attr.aria-invalid]="!!message('email')"
            [attr.aria-describedby]="message('email') ? prefix() + 'email-error' : null"
          />
          @if (message('email'); as m) {
            <p [id]="prefix() + 'email-error'" class="field-error">{{ m }}</p>
          }
        </div>
      </div>
    </fieldset>
  `,
})
export class StudentProfileFieldsComponent {
  readonly form = input.required<StudentFieldsForm>();
  readonly serverError = input.required<(path: string) => string | undefined>();
  readonly submitted = input(false);
  /** Id prefix, so the page and a dialog never share ids. */
  readonly prefix = input('s-');

  protected readonly text = ui.studentForm;
  protected readonly genders = ui.students.genders;
  protected readonly nameFields = ['firstName', 'lastName'] as const;
  protected readonly today = todayIso();
  /** Mirrors the birth date control for the live hint (the form is not signal-based). */
  private readonly birthDate = signal('');
  protected readonly age = computed(() => ageInfo(this.birthDate()));
  protected readonly adult = computed(() => this.age()?.minor === false);

  constructor() {
    effect((onCleanup) => {
      const control = this.form().controls.birthDate;
      this.birthDate.set(control.value);
      const sub = control.valueChanges.subscribe((value) => this.birthDate.set(value));
      onCleanup(() => sub.unsubscribe());
    });
  }

  protected message(field: StudentField): string | null {
    return fieldMessage(this.form(), this.serverError(), this.submitted(), field);
  }
}

export interface CopySource {
  name: string;
  phone: string;
  relationship: string;
  label: string;
}

/** Emergency contact section with optional "copy from guardian" buttons. */
@Component({
  selector: 'app-emergency-fields',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule, LucideCopy],
  template: `
    @let f = form();
    <fieldset
      class="card p-4 sm:p-6"
      tabindex="-1"
      [formGroup]="f"
      [attr.aria-describedby]="sectionError() ? prefix() + 'emergency-error' : null"
    >
      <legend class="sr-only">{{ text.emergencySection }}</legend>
      <h2 class="mb-4 text-base font-semibold" aria-hidden="true">{{ text.emergencySection }}</h2>
      @if (sectionError(); as m) {
        <p [id]="prefix() + 'emergency-error'" class="field-error mb-3">{{ m }}</p>
      }
      @if (sources().length > 0) {
        <div class="mb-4 flex flex-wrap gap-2">
          @for (source of sources(); track $index) {
            <button type="button" class="btn btn-secondary btn-sm" (click)="copy(source)">
              <svg lucideCopy></svg>{{ copyLabel(source) }}
            </button>
          }
        </div>
      }
      <p class="sr-only" role="status">{{ copiedText() }}</p>
      <div class="grid gap-4 sm:grid-cols-3">
        @for (field of fields; track field.control) {
          <div>
            <label class="field-label" [for]="prefix() + field.control">
              {{ text[field.label] }} <span class="text-destructive" aria-hidden="true">*</span>
            </label>
            <input
              [id]="prefix() + field.control"
              class="input"
              [type]="field.control === 'emergencyPhone' ? 'tel' : 'text'"
              [formControlName]="field.control"
              [attr.maxlength]="field.control === 'emergencyRelationship' ? 50 : 100"
              [placeholder]="
                field.control === 'emergencyRelationship'
                  ? text.emergencyRelationshipPlaceholder
                  : ''
              "
              autocomplete="off"
              aria-required="true"
              [attr.aria-invalid]="!!message(field.control)"
              [attr.aria-describedby]="
                message(field.control) ? prefix() + field.control + '-error' : null
              "
            />
            @if (message(field.control); as m) {
              <p [id]="prefix() + field.control + '-error'" class="field-error">{{ m }}</p>
            }
          </div>
        }
      </div>
    </fieldset>
  `,
})
export class EmergencyFieldsComponent {
  readonly form = input.required<StudentFieldsForm>();
  readonly serverError = input.required<(path: string) => string | undefined>();
  readonly submitted = input(false);
  readonly prefix = input('s-');
  readonly sources = input<CopySource[]>([]);
  readonly copied = output<void>();

  protected readonly text = ui.studentForm;
  protected readonly copiedText = signal('');
  protected readonly fields = [
    { control: 'emergencyName', label: 'emergencyName' },
    { control: 'emergencyPhone', label: 'emergencyPhone' },
    { control: 'emergencyRelationship', label: 'emergencyRelationship' },
  ] as const;

  protected sectionError(): string | undefined {
    return this.serverError()('emergencyContact');
  }

  protected message(field: StudentField): string | null {
    return fieldMessage(this.form(), this.serverError(), this.submitted(), field);
  }

  protected copyLabel(source: CopySource): string {
    return interpolate(this.text.copyFrom, { name: source.label });
  }

  protected copy(source: CopySource): void {
    const c = this.form().controls;
    c.emergencyName.setValue(source.name);
    c.emergencyPhone.setValue(source.phone);
    c.emergencyRelationship.setValue(source.relationship);
    for (const control of [c.emergencyName, c.emergencyPhone, c.emergencyRelationship]) {
      control.markAsDirty();
    }
    this.copiedText.set(this.text.copied);
    this.copied.emit();
  }
}
