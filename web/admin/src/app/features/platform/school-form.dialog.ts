import { DialogRef } from '@angular/cdk/dialog';
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
import { apiV1PlatformSchoolsPost } from '../../api/functions';
import { problemMessage, toProblem } from '../../core/api/problem';
import { errorMessages, ui } from '../../core/i18n/tr';
import { focusFirstInvalid } from '../../shared/forms/focus-first-invalid';
import { ServerFieldErrors } from '../../shared/forms/server-field-errors';
import { LOCALE } from '../../core/i18n/locale';

export const SCHOOL_FORM_TITLE_ID = 'school-form-title';

/** What the dialog closes with after a successful create; shown once, then dropped. */
export interface CreatedSchool {
  schoolName: string;
  adminName: string;
  temporaryPassword: string;
}
export const DEFAULT_TIME_ZONE = 'Europe/Istanbul';

/** Same rule as the API's Slug value object: lowercase words joined by single hyphens, 3–50 chars. */
export const SLUG_PATTERN = /^[a-z0-9]+(-[a-z0-9]+)*$/;

const trMap: Record<string, string> = { ç: 'c', ğ: 'g', ı: 'i', ö: 'o', ş: 's', ü: 'u' };

/** Suggests a slug from a school name: "Kartal Spor Kulübü" → "kartal-spor-kulubu". */
export function slugify(name: string): string {
  return name
    .toLocaleLowerCase(LOCALE)
    .replace(/[çğıöşü]/g, (c) => trMap[c])
    .normalize('NFD')
    .replace(/[̀-ͯ]/g, '')
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '')
    .slice(0, 50)
    .replace(/-+$/, '');
}

function timeZones(): string[] {
  const supported = (Intl as { supportedValuesOf?: (key: string) => string[] }).supportedValuesOf;
  const zones = supported ? supported('timeZone') : [];
  return zones.includes(DEFAULT_TIME_ZONE) ? zones : [DEFAULT_TIME_ZONE, ...zones];
}

type Field = 'name' | 'slug' | 'timeZone' | 'firstName' | 'lastName' | 'email';

const ids: Record<Field, string> = {
  name: 's-name',
  slug: 's-slug',
  timeZone: 's-timezone',
  firstName: 's-admin-first',
  lastName: 's-admin-last',
  email: 's-admin-email',
};

/** Platform Admin: create a school with its first admin (US-02). Closes with the API response. */
@Component({
  selector: 'app-school-form-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule],
  template: `
    <form
      class="max-h-[calc(100dvh-2rem)] w-[min(34rem,calc(100vw-2rem))] overflow-y-auto rounded-lg border border-border bg-background p-6 shadow-lg"
      [formGroup]="form"
      (ngSubmit)="save()"
      novalidate
    >
      <h2 [id]="titleId" class="text-lg font-semibold">{{ text.add }}</h2>
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
            maxlength="150"
            autocomplete="organization"
            [attr.aria-invalid]="!!message('name')"
            [attr.aria-describedby]="message('name') ? ids.name + '-error' : null"
          />
          @if (message('name'); as m) {
            <p [id]="ids.name + '-error'" class="field-error">{{ m }}</p>
          }
        </div>
        <div>
          <label class="field-label" [for]="ids.slug">{{ text.slug }}</label>
          <input
            [id]="ids.slug"
            class="input font-mono"
            formControlName="slug"
            maxlength="50"
            autocapitalize="none"
            autocomplete="off"
            spellcheck="false"
            [attr.aria-invalid]="!!message('slug')"
            [attr.aria-describedby]="ids.slug + '-hint ' + ids.slug + '-status'"
          />
          <p [id]="ids.slug + '-hint'" class="mt-1.5 text-xs text-muted-foreground">
            {{ text.slugHint }}
          </p>
          <p [id]="ids.slug + '-status'" aria-live="polite">
            @if (message('slug'); as m) {
              <span class="field-error block">{{ m }}</span>
            } @else if (slugChecked() && form.controls.slug.value) {
              <span class="mt-1.5 block text-xs text-success">{{ text.slugValid }}</span>
            }
          </p>
        </div>
        <div>
          <label class="field-label" [for]="ids.timeZone">{{ text.timeZone }}</label>
          <select
            [id]="ids.timeZone"
            class="input"
            formControlName="timeZone"
            [attr.aria-invalid]="!!message('timeZone')"
            [attr.aria-describedby]="message('timeZone') ? ids.timeZone + '-error' : null"
          >
            @for (zone of zones; track zone) {
              <option [value]="zone">{{ zone }}</option>
            }
          </select>
          @if (message('timeZone'); as m) {
            <p [id]="ids.timeZone + '-error'" class="field-error">{{ m }}</p>
          }
        </div>
      </div>

      <fieldset class="mt-6 border-t border-border pt-4">
        <legend class="pr-2 text-sm font-semibold">{{ text.adminSection }}</legend>
        <p id="s-admin-hint" class="text-xs text-muted-foreground">{{ text.adminHint }}</p>
        <div class="mt-3 grid gap-4 sm:grid-cols-2">
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
            <input
              [id]="ids.email"
              type="email"
              class="input"
              formControlName="email"
              autocomplete="off"
              autocapitalize="none"
              (blur)="trimEmail()"
              [attr.aria-invalid]="!!message('email')"
              [attr.aria-describedby]="
                message('email') ? ids.email + '-error s-admin-hint' : 's-admin-hint'
              "
            />
            @if (message('email'); as m) {
              <p [id]="ids.email + '-error'" class="field-error">{{ m }}</p>
            }
          </div>
        </div>
      </fieldset>

      <div class="mt-6 flex flex-wrap justify-end gap-2">
        <button type="button" class="btn btn-secondary" (click)="ref.close()">
          {{ actions.cancel }}
        </button>
        <button type="submit" class="btn btn-primary" [disabled]="saving()">
          {{ text.add }}
        </button>
      </div>
    </form>
  `,
})
export class SchoolFormDialog {
  private readonly api = inject(Api);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  protected readonly ref = inject<DialogRef<CreatedSchool>>(DialogRef);

  protected readonly text = ui.schools;
  protected readonly actions = ui.actions;
  protected readonly ids = ids;
  protected readonly titleId = SCHOOL_FORM_TITLE_ID;
  protected readonly zones = timeZones();
  protected readonly saving = signal(false);
  private readonly submitted = signal(false);
  protected readonly error = signal<string | null>(null);
  private readonly server = new ServerFieldErrors<Field>(
    {
      name: 'name',
      slug: 'slug',
      timeZone: 'timeZone',
      firstName: 'admin.firstName',
      lastName: 'admin.lastName',
      email: 'admin.email',
    },
    {
      'School.SlugTaken': 'slug',
      'User.EmailTaken': 'email',
      'School.InvalidTimeZone': 'timeZone',
    },
  );

  protected readonly form = inject(FormBuilder).nonNullable.group({
    name: ['', [Validators.required, Validators.maxLength(150)]],
    slug: ['', [Validators.required, Validators.pattern(SLUG_PATTERN), Validators.minLength(3)]],
    timeZone: [DEFAULT_TIME_ZONE, Validators.required],
    firstName: ['', [Validators.required, Validators.maxLength(100)]],
    lastName: ['', [Validators.required, Validators.maxLength(100)]],
    email: ['', [Validators.required, Validators.email]],
  });

  constructor() {
    const destroyRef = inject(DestroyRef);
    this.server.watch(this.form.controls, destroyRef);
    // Suggest a slug from the name until the user edits the slug themselves.
    const subscription = this.form.controls.name.valueChanges.subscribe((name) => {
      const slug = this.form.controls.slug;
      if (!slug.dirty) {
        slug.setValue(slugify(name));
      }
    });
    destroyRef.onDestroy(() => subscription.unsubscribe());
  }

  protected slugChecked(): boolean {
    return this.form.controls.slug.dirty || this.submitted();
  }

  /** Pasted addresses often carry spaces; trim before the e-mail rule runs. */
  protected trimEmail(): void {
    const email = this.form.controls.email;
    if (email.value !== email.value.trim()) {
      email.setValue(email.value.trim());
    }
  }

  /** Server error first, then the client rule; client errors appear once the field was used. */
  protected message(field: Field): string | null {
    const server = this.server.get(field);
    if (server) {
      return server;
    }
    const control = this.form.controls[field];
    // A suggested slug is not judged; only one the user typed (or a submitted form) is.
    const shown = field === 'slug' ? this.slugChecked() : control.touched || control.dirty;
    if (!shown || control.valid) {
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
    if (field === 'slug') {
      return errorMessages['Slug.Invalid'];
    }
    return errorMessages['Field.Invalid'];
  }

  protected async save(): Promise<void> {
    if (this.saving()) {
      return;
    }
    this.submitted.set(true);
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
    try {
      const created = await this.api.invoke(apiV1PlatformSchoolsPost, {
        body: {
          name: v.name.trim(),
          slug: v.slug,
          timeZone: v.timeZone,
          admin: {
            firstName: v.firstName.trim(),
            lastName: v.lastName.trim(),
            email: v.email.trim(),
          },
        },
      });
      this.ref.close({
        schoolName: created.school.name,
        adminName: `${v.firstName.trim()} ${v.lastName.trim()}`,
        temporaryPassword: created.temporaryPassword,
      });
    } catch (err) {
      const problem = toProblem(err);
      this.server.apply(problem);
      this.error.set(problemMessage(problem));
      this.saving.set(false);
      this.focusFirstInvalid();
    }
  }

  private focusFirstInvalid(): void {
    focusFirstInvalid(
      this.host.nativeElement,
      (Object.keys(ids) as Field[]).map((f) => [ids[f], !!this.message(f)] as const),
    );
  }
}
