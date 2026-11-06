import { ChangeDetectionStrategy, Component, inject, input, output } from '@angular/core';
import { ReactiveFormsModule } from '@angular/forms';
import { LucideLink, LucideSearch, LucideTrash2, LucideUserPlus } from '@lucide/angular';

import { Api } from '../../api/api';
import { formatPhone } from '../../core/format/format';
import { interpolate, ui } from '../../core/i18n/tr';
import { GuardianEntry } from './guardian-entry';
import { clientMessage } from './path-errors';

type EntryField = 'firstName' | 'lastName' | 'phone' | 'email' | 'line' | 'district' | 'city';

/** API path of each field below `guardians[i].newGuardian`. */
const apiPaths: Record<EntryField, string> = {
  firstName: 'firstName',
  lastName: 'lastName',
  phone: 'phone',
  email: 'email',
  line: 'address.line',
  district: 'address.district',
  city: 'address.city',
};

/** One guardian slot: look up by phone and link, or enter a new guardian. */
@Component({
  selector: 'app-guardian-entry',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule, LucideLink, LucideSearch, LucideTrash2, LucideUserPlus],
  template: `
    @let e = entry();
    @let id = 'gd-' + e.key;
    <fieldset
      tabindex="-1"
      class="rounded-lg border border-border p-4"
      [formGroup]="e.form"
      [attr.aria-describedby]="slotError() ? id + '-slot-error' : null"
    >
      <legend class="px-1 text-sm font-semibold">{{ label() }}</legend>
      <div class="flex justify-end">
        <button
          type="button"
          class="btn btn-ghost btn-sm hover:!bg-destructive/10 hover:!text-destructive"
          [attr.aria-label]="text.removeGuardian + ': ' + label()"
          (click)="remove.emit()"
        >
          <svg lucideTrash2></svg>{{ text.removeGuardian }}
        </button>
      </div>

      @if (slotError(); as m) {
        <p [id]="id + '-slot-error'" class="field-error mb-2">{{ m }}</p>
      }

      @switch (e.mode()) {
        @case ('lookup') {
          <label class="field-label" [for]="id + '-lookup'">{{ text.lookupPhone }}</label>
          <div class="flex flex-col gap-2 sm:flex-row">
            <input
              [id]="id + '-lookup'"
              type="tel"
              inputmode="tel"
              class="input min-w-0 flex-1"
              formControlName="lookupPhone"
              autocomplete="off"
              [attr.aria-invalid]="!!e.lookupError()"
              [attr.aria-describedby]="
                e.lookupError() ? id + '-lookup-hint ' + id + '-lookup-error' : id + '-lookup-hint'
              "
              (keydown.enter)="$event.preventDefault(); lookup()"
            />
            <button
              type="button"
              class="btn btn-secondary"
              [disabled]="e.searching()"
              (click)="lookup()"
            >
              <svg lucideSearch></svg>{{ e.searching() ? text.lookingUp : text.lookup }}
            </button>
          </div>
          <p [id]="id + '-lookup-hint'" class="mt-1.5 text-xs text-muted-foreground">
            {{ text.phoneHint }}
          </p>
          @if (e.lookupError(); as m) {
            <p [id]="id + '-lookup-error'" class="field-error">{{ m }}</p>
          }
          <div role="status" class="mt-3">
            @if (e.found(); as guardian) {
              <div class="rounded-lg border border-primary/30 bg-primary/5 p-3 text-sm">
                <p>{{ foundText(guardian.firstName + ' ' + guardian.lastName) }}</p>
                <p class="font-mono text-xs text-muted-foreground">{{ phone(guardian.phone) }}</p>
                <button
                  type="button"
                  class="btn btn-primary btn-sm mt-2"
                  (click)="e.link(guardian)"
                >
                  <svg lucideLink></svg>{{ text.link }}
                </button>
              </div>
            }
          </div>
          <button type="button" class="btn btn-ghost btn-sm mt-2" (click)="e.startNew()">
            <svg lucideUserPlus></svg>{{ text.enterNew }}
          </button>
        }
        @case ('linked') {
          <div class="rounded-lg border border-success/30 bg-success/10 p-3 text-sm">
            <p class="font-medium text-foreground">{{ linkedText() }}</p>
            <p class="font-mono text-xs text-muted-foreground">
              {{ phone(e.linked()?.phone ?? '') }}
            </p>
            <button type="button" class="btn btn-ghost btn-sm mt-1" (click)="e.reset()">
              {{ text.unlink }}
            </button>
          </div>
        }
        @case ('new') {
          @if (e.found() === null) {
            <p role="status" class="mb-3 text-sm text-muted-foreground">{{ text.notFound }}</p>
          }
          <div class="grid gap-3 sm:grid-cols-2">
            @for (field of nameFields; track field) {
              <div>
                <label class="field-label" [for]="id + '-' + field">{{ text[field] }}</label>
                <input
                  [id]="id + '-' + field"
                  class="input"
                  [formControlName]="field"
                  maxlength="100"
                  autocomplete="off"
                  [attr.aria-invalid]="!!message(field)"
                  [attr.aria-describedby]="message(field) ? id + '-' + field + '-error' : null"
                />
                @if (message(field); as m) {
                  <p [id]="id + '-' + field + '-error'" class="field-error">{{ m }}</p>
                }
              </div>
            }
            <div>
              <label class="field-label" [for]="id + '-phone'">{{ text.phone }}</label>
              <input
                [id]="id + '-phone'"
                type="tel"
                inputmode="tel"
                class="input"
                formControlName="phone"
                autocomplete="off"
                [attr.aria-invalid]="!!message('phone')"
                [attr.aria-describedby]="message('phone') ? id + '-phone-error' : null"
              />
              @if (message('phone'); as m) {
                <p [id]="id + '-phone-error'" class="field-error">{{ m }}</p>
              }
            </div>
            <div>
              <label class="field-label" [for]="id + '-email'">{{ text.email }}</label>
              <input
                [id]="id + '-email'"
                type="email"
                class="input"
                formControlName="email"
                autocomplete="off"
                autocapitalize="none"
                [attr.aria-invalid]="!!message('email')"
                [attr.aria-describedby]="message('email') ? id + '-email-error' : null"
              />
              @if (message('email'); as m) {
                <p [id]="id + '-email-error'" class="field-error">{{ m }}</p>
              }
            </div>
          </div>
          @if (e.form.controls.hasAddress.value) {
            <div class="mt-3 grid gap-3 sm:grid-cols-2">
              @for (field of addressFields; track field) {
                <div [class.sm:col-span-2]="field === 'line'">
                  <label class="field-label" [for]="id + '-' + field">{{
                    text[field === 'line' ? 'addressLine' : field]
                  }}</label>
                  <input
                    [id]="id + '-' + field"
                    class="input"
                    [formControlName]="field"
                    [attr.maxlength]="field === 'line' ? 200 : 60"
                    autocomplete="off"
                    [attr.aria-invalid]="!!message(field)"
                    [attr.aria-describedby]="message(field) ? id + '-' + field + '-error' : null"
                  />
                  @if (message(field); as m) {
                    <p [id]="id + '-' + field + '-error'" class="field-error">{{ m }}</p>
                  }
                </div>
              }
            </div>
            <button type="button" class="btn btn-ghost btn-sm mt-2" (click)="e.setAddress(false)">
              {{ text.removeAddress }}
            </button>
          } @else {
            <button type="button" class="btn btn-ghost btn-sm mt-2" (click)="e.setAddress(true)">
              {{ text.addAddress }}
            </button>
          }
          <button type="button" class="btn btn-ghost btn-sm mt-2" (click)="e.reset()">
            <svg lucideSearch></svg>{{ text.lookup }}
          </button>
        }
      }

      <div class="mt-3 max-w-xs">
        <label class="field-label" [for]="id + '-relationship'">{{ text.relationship }}</label>
        <select [id]="id + '-relationship'" class="input" formControlName="relationship">
          @for (r of relationships; track r) {
            <option [value]="r">{{ text.relationships[r] }}</option>
          }
        </select>
      </div>
    </fieldset>
  `,
})
export class GuardianEntryComponent {
  private readonly api = inject(Api);

  readonly entry = input.required<GuardianEntry>();
  readonly index = input.required<number>();
  /** Server error for an API path; the parent owns the error state. */
  readonly serverError = input.required<(path: string) => string | undefined>();
  readonly submitted = input(false);
  readonly remove = output<void>();

  protected readonly text = ui.studentForm;
  protected readonly relationships = ['Mother', 'Father', 'Other'] as const;
  protected readonly nameFields = ['firstName', 'lastName'] as const;
  protected readonly addressFields = ['line', 'district', 'city'] as const;

  protected label(): string {
    return interpolate(this.text.guardianLabel, { n: this.index() + 1 });
  }

  protected phone(value: string): string {
    return formatPhone(value);
  }

  protected foundText(name: string): string {
    return interpolate(this.text.found, { name });
  }

  protected linkedText(): string {
    const g = this.entry().linked();
    return g ? interpolate(this.text.linked, { name: `${g.firstName} ${g.lastName}` }) : '';
  }

  /** Errors that concern the whole slot: an incomplete slot on submit, or the API's slot error. */
  protected slotError(): string | undefined {
    const base = `guardians[${this.index()}]`;
    const server =
      this.serverError()(base) ??
      this.serverError()(`${base}.guardianId`) ??
      this.serverError()(`${base}.newGuardian`) ??
      this.serverError()(`${base}.relationship`);
    if (server) {
      return server;
    }
    return this.submitted() && this.entry().mode() === 'lookup'
      ? this.text.guardianIncomplete
      : undefined;
  }

  protected message(field: EntryField): string | null {
    const server = this.serverError()(`guardians[${this.index()}].newGuardian.${apiPaths[field]}`);
    if (server) {
      return server;
    }
    const control = this.entry().form.controls[field];
    return control.touched || control.dirty || this.submitted() ? clientMessage(control) : null;
  }

  protected lookup(): Promise<void> {
    return this.entry().lookup(this.api);
  }
}

export { apiPaths as guardianApiPaths };
