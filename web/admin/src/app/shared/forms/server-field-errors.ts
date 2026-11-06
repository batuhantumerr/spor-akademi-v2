import { DestroyRef, signal } from '@angular/core';
import { AbstractControl } from '@angular/forms';

import { ApiProblem, fieldErrors, problemMessage } from '../../core/api/problem';

/**
 * Field errors reported by the API for a reactive form. `paths` maps each control to the API field
 * path (e.g. `adminEmail` → `admin.email`); `codeFields` routes coded problems without a field map
 * (e.g. 409 `User.EmailTaken`) to a control. An error is cleared as soon as its field changes.
 */
export class ServerFieldErrors<F extends string> {
  private readonly errors = signal<Partial<Record<F, string>>>({});

  constructor(
    private readonly paths: Record<F, string>,
    private readonly codeFields: Readonly<Record<string, F>> = {},
  ) {}

  get(field: F): string | undefined {
    return this.errors()[field];
  }

  /** Stores the problem's field errors; returns true when at least one field got an error. */
  apply(problem: ApiProblem): boolean {
    const byPath = fieldErrors(problem);
    const next: Partial<Record<F, string>> = {};
    for (const field of Object.keys(this.paths) as F[]) {
      const message = byPath[this.paths[field]];
      if (message) {
        next[field] = message;
      }
    }
    const coded = this.codeFields[problem.code];
    if (coded && !next[coded]) {
      next[coded] = problemMessage(problem);
    }
    this.errors.set(next);
    return Object.keys(next).length > 0;
  }

  reset(): void {
    this.errors.set({});
  }

  /** Clears a field's server error when the user edits that field. */
  watch(controls: Record<F, AbstractControl>, destroyRef: DestroyRef): void {
    for (const field of Object.keys(this.paths) as F[]) {
      const subscription = controls[field].valueChanges.subscribe(() => {
        if (this.errors()[field]) {
          this.errors.update((errors) => {
            const rest = { ...errors };
            delete rest[field];
            return rest;
          });
        }
      });
      destroyRef.onDestroy(() => subscription.unsubscribe());
    }
  }
}
