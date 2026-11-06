import { signal } from '@angular/core';
import { AbstractControl } from '@angular/forms';

import { ApiProblem, fieldErrors, problemMessage } from '../../core/api/problem';
import { errorMessages } from '../../core/i18n/tr';

/**
 * API errors keyed by the API's field path (`firstName`, `emergencyContact.phone`,
 * `guardians[1].newGuardian.email`, `guardians` …). Coded problems without a field map are put on
 * a path through `codePaths` (e.g. `Student.GuardianRequired` → `guardians`).
 */
export class PathErrors {
  private readonly errors = signal<Record<string, string>>({});

  constructor(private readonly codePaths: Readonly<Record<string, string>> = {}) {}

  get(path: string): string | undefined {
    return this.errors()[path];
  }

  /** Any error at or below a path, e.g. a whole guardian slot. */
  hasUnder(prefix: string): boolean {
    return Object.keys(this.errors()).some((k) => k === prefix || k.startsWith(prefix));
  }

  apply(problem: ApiProblem): void {
    const next = { ...fieldErrors(problem) };
    const path = this.codePaths[problem.code];
    if (path && !next[path]) {
      next[path] = problemMessage(problem);
    }
    this.errors.set(next);
  }

  set(path: string, message: string): void {
    this.errors.update((all) => ({ ...all, [path]: message }));
  }

  clear(path: string): void {
    if (this.errors()[path] !== undefined) {
      this.errors.update((all) => {
        const rest = { ...all };
        delete rest[path];
        return rest;
      });
    }
  }

  /** Drops every error at or below a path (guardian slots renumber when one is removed). */
  clearUnder(prefix: string): void {
    this.errors.update((all) =>
      Object.fromEntries(Object.entries(all).filter(([k]) => !k.startsWith(prefix))),
    );
  }

  reset(): void {
    this.errors.set({});
  }
}

/** Client-side message for a control, or null when it is valid. */
export function clientMessage(control: AbstractControl): string | null {
  if (control.disabled || control.valid) {
    return null;
  }
  if (control.hasError('required')) {
    return errorMessages['Field.Required'];
  }
  if (control.hasError('maxlength')) {
    return errorMessages['Field.TooLong'];
  }
  if (control.hasError('email')) {
    return errorMessages['Email.Invalid'];
  }
  if (control.hasError('phone')) {
    return errorMessages['PhoneNumber.Invalid'];
  }
  return errorMessages['Field.Invalid'];
}
