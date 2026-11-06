import { signal } from '@angular/core';
import {
  AbstractControl,
  FormControl,
  FormGroup,
  ValidationErrors,
  Validators,
} from '@angular/forms';

import { Api } from '../../api/api';
import { apiV1GuardiansGet } from '../../api/functions';
import { GuardianDto, GuardianLinkInput, GuardianRelationship } from '../../api/models';
import { fieldErrors, problemMessage, toProblem } from '../../core/api/problem';
import { errorMessages } from '../../core/i18n/tr';
import { optionalTurkishPhone } from '../../shared/forms/validators';

/**
 * One guardian slot of the registration form (US-13). Starts in `lookup`: the user searches by
 * phone and links an existing guardian (`linked`), or enters a new one (`new`).
 */
export type GuardianMode = 'lookup' | 'linked' | 'new';

let nextKey = 1;

function required(control: AbstractControl): ValidationErrors | null {
  return String(control.value ?? '').trim() === '' ? { required: true } : null;
}

export class GuardianEntry {
  readonly key = nextKey++;
  readonly mode = signal<GuardianMode>('lookup');
  /** Result of the last lookup: a guardian, null (not found), or undefined (not searched yet). */
  readonly found = signal<GuardianDto | null | undefined>(undefined);
  readonly linked = signal<GuardianDto | null>(null);
  readonly searching = signal(false);
  readonly lookupError = signal<string | null>(null);

  readonly form = new FormGroup({
    lookupPhone: new FormControl('', { nonNullable: true }),
    relationship: new FormControl<GuardianRelationship>('Mother', { nonNullable: true }),
    firstName: new FormControl('', {
      nonNullable: true,
      validators: [required, Validators.maxLength(100)],
    }),
    lastName: new FormControl('', {
      nonNullable: true,
      validators: [required, Validators.maxLength(100)],
    }),
    phone: new FormControl('', { nonNullable: true, validators: [required, optionalTurkishPhone] }),
    email: new FormControl('', {
      nonNullable: true,
      validators: [Validators.email, Validators.maxLength(254)],
    }),
    hasAddress: new FormControl(false, { nonNullable: true }),
    line: new FormControl('', {
      nonNullable: true,
      validators: [required, Validators.maxLength(200)],
    }),
    district: new FormControl('', {
      nonNullable: true,
      validators: [required, Validators.maxLength(60)],
    }),
    city: new FormControl('', {
      nonNullable: true,
      validators: [required, Validators.maxLength(60)],
    }),
  });

  constructor() {
    this.syncEnabled();
  }

  /** Looks the phone up; a found guardian is offered for linking, otherwise "new" opens. */
  async lookup(api: Api): Promise<void> {
    const phone = this.form.controls.lookupPhone.value.trim();
    if (this.searching()) {
      return;
    }
    if (phone === '' || optionalTurkishPhone(this.form.controls.lookupPhone) !== null) {
      this.lookupError.set(errorMessages[phone === '' ? 'Field.Required' : 'PhoneNumber.Invalid']);
      return;
    }
    this.searching.set(true);
    this.lookupError.set(null);
    try {
      const result = await api.invoke(apiV1GuardiansGet, { phone });
      const guardian = result.items[0] ?? null;
      this.found.set(guardian);
      if (!guardian) {
        this.startNew(phone);
      }
    } catch (err) {
      const problem = toProblem(err);
      this.lookupError.set(fieldErrors(problem)['phone'] ?? problemMessage(problem));
    } finally {
      this.searching.set(false);
    }
  }

  link(guardian: GuardianDto): void {
    this.linked.set(guardian);
    this.mode.set('linked');
    this.syncEnabled();
  }

  /** Back to searching (e.g. linked the wrong person). */
  reset(): void {
    this.linked.set(null);
    this.found.set(undefined);
    this.mode.set('lookup');
    this.syncEnabled();
  }

  startNew(phone = this.form.controls.lookupPhone.value.trim()): void {
    this.linked.set(null);
    this.mode.set('new');
    if (!this.form.controls.phone.value) {
      this.form.controls.phone.setValue(phone);
    }
    this.syncEnabled();
  }

  setAddress(enabled: boolean): void {
    this.form.controls.hasAddress.setValue(enabled);
    this.syncEnabled();
  }

  /** Name and phone for "copy to emergency contact", when known. */
  contact(): { name: string; phone: string } | null {
    const linked = this.linked();
    if (this.mode() === 'linked' && linked) {
      return { name: `${linked.firstName} ${linked.lastName}`, phone: linked.phone };
    }
    if (this.mode() === 'new') {
      const v = this.form.getRawValue();
      const name = `${v.firstName.trim()} ${v.lastName.trim()}`.trim();
      return name && v.phone.trim() ? { name, phone: v.phone.trim() } : null;
    }
    return null;
  }

  /** Complete and valid enough to send. */
  isReady(): boolean {
    return this.mode() === 'linked' || (this.mode() === 'new' && this.form.valid);
  }

  toInput(): GuardianLinkInput {
    const relationship = this.form.controls.relationship.value;
    const linked = this.linked();
    if (this.mode() === 'linked' && linked) {
      return { guardianId: linked.id, newGuardian: null, relationship };
    }
    const v = this.form.getRawValue();
    return {
      guardianId: null,
      relationship,
      newGuardian: {
        firstName: v.firstName.trim(),
        lastName: v.lastName.trim(),
        phone: v.phone.trim(),
        email: v.email.trim() === '' ? null : v.email.trim(),
        address: v.hasAddress
          ? { line: v.line.trim(), district: v.district.trim(), city: v.city.trim() }
          : null,
      },
    };
  }

  /** Only the controls of the current mode take part in validation. */
  private syncEnabled(): void {
    const c = this.form.controls;
    const isNew = this.mode() === 'new';
    for (const control of [c.firstName, c.lastName, c.phone, c.email, c.hasAddress]) {
      if (isNew) {
        control.enable({ emitEvent: false });
      } else {
        control.disable({ emitEvent: false });
      }
    }
    for (const control of [c.line, c.district, c.city]) {
      if (isNew && c.hasAddress.value) {
        control.enable({ emitEvent: false });
      } else {
        control.disable({ emitEvent: false });
      }
    }
  }
}
