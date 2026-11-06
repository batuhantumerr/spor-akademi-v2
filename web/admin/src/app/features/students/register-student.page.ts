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
import { Router, RouterLink } from '@angular/router';
import { LucideArrowLeft, LucidePlus } from '@lucide/angular';

import { Api } from '../../api/api';
import { apiV1StudentsPost } from '../../api/functions';
import { formatPhone } from '../../core/format/format';
import { errorMessages, ui } from '../../core/i18n/tr';
import { ToastService } from '../../shared/ui/toast.service';
import { toProblem, problemMessage } from '../../core/api/problem';
import { PageHeaderComponent } from '../../shared/ui/page-header.component';
import { GuardianEntry } from './guardian-entry';
import { GuardianEntryComponent } from './guardian-entry.component';
import { PathErrors } from './path-errors';
import {
  CopySource,
  EmergencyFieldsComponent,
  StudentProfileFieldsComponent,
} from './student-fields.component';
import {
  StudentField,
  ageInfo,
  studentApiPaths,
  studentBody,
  studentCodePaths,
  studentFieldsForm,
} from './student-fields';

/** Same limit as the API (Student.MaxGuardians). */
export const MAX_GUARDIANS = 4;

/** US-10 / US-13: register a student with guardians and an emergency contact (a page: long form). */
@Component({
  selector: 'app-register-student-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    RouterLink,
    PageHeaderComponent,
    StudentProfileFieldsComponent,
    EmergencyFieldsComponent,
    GuardianEntryComponent,
    LucideArrowLeft,
    LucidePlus,
  ],
  template: `
    <a routerLink="/students" class="btn btn-ghost btn-sm mb-2 -ml-2">
      <svg lucideArrowLeft></svg>{{ text.back }}
    </a>
    <app-page-header [title]="text.title" [description]="text.description" />

    <form class="mx-auto max-w-3xl space-y-4" (submit)="$event.preventDefault(); save()" novalidate>
      @if (error()) {
        <p
          role="alert"
          class="rounded-lg border border-destructive/30 bg-destructive/10 p-3 text-sm text-destructive"
        >
          {{ error() }}
        </p>
      }

      <app-student-profile-fields
        [form]="form"
        [serverError]="serverError"
        [submitted]="submitted()"
      />

      <fieldset
        class="card p-4 sm:p-6"
        id="guardians-section"
        tabindex="-1"
        [attr.aria-describedby]="'guardians-hint' + (guardiansError() ? ' guardians-error' : '')"
      >
        <legend class="sr-only">{{ text.guardiansSection }}</legend>
        <h2 class="mb-1 text-base font-semibold" aria-hidden="true">{{ text.guardiansSection }}</h2>
        <p id="guardians-hint" class="mb-4 text-xs text-muted-foreground">
          {{ text.guardiansHint }}
        </p>
        @if (guardiansError(); as m) {
          <p id="guardians-error" class="field-error mb-3">{{ m }}</p>
        }
        @if (entries().length === 0) {
          <p class="mb-3 text-sm text-muted-foreground">{{ text.noGuardians }}</p>
        }
        <div class="space-y-3">
          @for (entry of entries(); track entry.key; let i = $index) {
            <app-guardian-entry
              [entry]="entry"
              [index]="i"
              [serverError]="serverError"
              [submitted]="submitted()"
              (remove)="removeGuardian(entry)"
            />
          }
        </div>
        <button
          type="button"
          class="btn btn-secondary mt-3"
          [disabled]="entries().length >= maxGuardians"
          [attr.aria-describedby]="entries().length >= maxGuardians ? 'guardians-max' : null"
          (click)="addGuardian()"
        >
          <svg lucidePlus></svg>{{ text.addGuardian }}
        </button>
        @if (entries().length >= maxGuardians) {
          <p id="guardians-max" class="mt-1.5 text-xs text-muted-foreground">
            {{ text.maxGuardians }}
          </p>
        }
      </fieldset>

      <app-emergency-fields
        [form]="form"
        [serverError]="serverError"
        [submitted]="submitted()"
        [sources]="copySources()"
      />

      <div class="flex justify-end">
        <button type="submit" class="btn btn-primary w-full sm:w-auto" [disabled]="saving()">
          {{ saving() ? text.saving : text.submit }}
        </button>
      </div>
    </form>
  `,
})
export class RegisterStudentPage {
  private readonly api = inject(Api);
  private readonly router = inject(Router);
  private readonly toasts = inject(ToastService);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly injector = inject(Injector);
  private readonly destroyRef = inject(DestroyRef);

  protected readonly text = ui.studentForm;
  protected readonly maxGuardians = MAX_GUARDIANS;
  protected readonly form = studentFieldsForm();
  protected readonly entries = signal<GuardianEntry[]>([]);
  protected readonly saving = signal(false);
  protected readonly submitted = signal(false);
  protected readonly error = signal<string | null>(null);
  /** Client-side section problem (minor without a guardian), shown until guardians change. */
  private readonly guardiansClientError = signal<string | null>(null);
  /** Bumped on guardian edits so the copy buttons follow typed names. */
  private readonly guardianEdits = signal(0);
  private readonly errors = new PathErrors(studentCodePaths);
  protected readonly serverError = (path: string) => this.errors.get(path);

  protected readonly guardiansError = computed(
    () => this.errors.get('guardians') ?? this.guardiansClientError() ?? undefined,
  );

  protected readonly copySources = computed<CopySource[]>(() => {
    this.guardianEdits();
    return this.entries().flatMap((entry) => {
      const contact = entry.contact();
      if (!contact) {
        return [];
      }
      const relationship = entry.form.controls.relationship.value;
      return [
        {
          name: contact.name,
          phone: formatPhone(contact.phone),
          relationship: this.text.emergencyRelations[relationship],
          label: contact.name,
        },
      ];
    });
  });

  constructor() {
    // A field's API error belongs to the value it was reported for.
    for (const field of Object.keys(studentApiPaths) as StudentField[]) {
      const sub = this.form.controls[field].valueChanges.subscribe(() => {
        this.errors.clear(studentApiPaths[field]);
        if (field.startsWith('emergency')) {
          this.errors.clear('emergencyContact');
        }
        if (field === 'phone' || field === 'birthDate') {
          this.errors.clear('phone');
        }
      });
      this.destroyRef.onDestroy(() => sub.unsubscribe());
    }
  }

  protected addGuardian(): void {
    if (this.entries().length >= MAX_GUARDIANS) {
      return;
    }
    const entry = new GuardianEntry();
    const sub = entry.form.valueChanges.subscribe(() => {
      const index = this.entries().indexOf(entry);
      this.errors.clearUnder(`guardians[${index}]`);
      this.guardianEdits.update((n) => n + 1);
    });
    this.destroyRef.onDestroy(() => sub.unsubscribe());
    this.entries.update((list) => [...list, entry]);
    this.onGuardiansChanged();
    afterNextRender(() => this.focus(`gd-${entry.key}-lookup`), { injector: this.injector });
  }

  protected removeGuardian(entry: GuardianEntry): void {
    this.entries.update((list) => list.filter((e) => e !== entry));
    // Slot numbers shift, so per-slot API errors no longer line up.
    this.errors.clearUnder('guardians[');
    this.onGuardiansChanged();
    afterNextRender(() => this.focus('guardians-section'), { injector: this.injector });
  }

  protected async save(): Promise<void> {
    if (this.saving()) {
      return;
    }
    this.submitted.set(true);
    this.form.markAllAsTouched();
    const invalid = this.clientProblems();
    if (invalid) {
      this.focus(invalid);
      return;
    }
    this.saving.set(true);
    this.error.set(null);
    this.errors.reset();
    try {
      const student = await this.api.invoke(apiV1StudentsPost, {
        body: { ...studentBody(this.form), guardians: this.entries().map((e) => e.toInput()) },
      });
      this.toasts.success(this.text.created);
      void this.router.navigate(['/students', student.id]);
    } catch (err) {
      const problem = toProblem(err);
      this.errors.apply(problem);
      this.error.set(problemMessage(problem));
      this.saving.set(false);
      afterNextRender(() => this.focusFirstError(), { injector: this.injector });
    }
  }

  /** Returns the id to focus when something must be fixed first, or null. */
  private clientProblems(): string | null {
    const order: StudentField[] = [
      'firstName',
      'lastName',
      'birthDate',
      'gender',
      'phone',
      'email',
    ];
    const age = ageInfo(this.form.controls.birthDate.value);
    const firstInvalid = order.find((f) => this.form.controls[f].invalid);
    if (firstInvalid) {
      return `s-${firstInvalid}`;
    }
    if (age && !age.minor && this.form.controls.phone.value.trim() === '') {
      this.errors.apply({ code: 'Student.PhoneRequiredForAdult' });
      return 's-phone';
    }
    if (age?.minor && this.entries().length === 0) {
      this.guardiansClientError.set(errorMessages['Student.GuardianRequired']);
      return 'guardians-section';
    }
    const notReady = this.entries().find((e) => !e.isReady());
    if (notReady) {
      notReady.form.markAllAsTouched();
      return notReady.mode() === 'lookup'
        ? `gd-${notReady.key}-lookup`
        : this.firstEntryField(notReady);
    }
    const emergency = (['emergencyName', 'emergencyPhone', 'emergencyRelationship'] as const).find(
      (f) => this.form.controls[f].invalid,
    );
    return emergency ? `s-${emergency}` : null;
  }

  private firstEntryField(entry: GuardianEntry): string {
    const c = entry.form.controls;
    const field = (
      ['firstName', 'lastName', 'phone', 'email', 'line', 'district', 'city'] as const
    ).find((f) => c[f].enabled && c[f].invalid);
    return `gd-${entry.key}-${field ?? 'firstName'}`;
  }

  private onGuardiansChanged(): void {
    this.guardiansClientError.set(null);
    this.errors.clear('guardians');
  }

  /** After an API error: the first field or section marked invalid in the DOM. */
  private focusFirstError(): void {
    const target = this.host.nativeElement.querySelector<HTMLElement>(
      '[aria-invalid="true"], fieldset[aria-describedby*="error"]',
    );
    target?.focus();
  }

  private focus(id: string): void {
    this.host.nativeElement.querySelector<HTMLElement>(`[id="${id}"]`)?.focus();
  }
}
