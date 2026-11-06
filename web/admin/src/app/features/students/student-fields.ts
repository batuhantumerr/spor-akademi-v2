import {
  AbstractControl,
  FormControl,
  FormGroup,
  ValidationErrors,
  Validators,
} from '@angular/forms';

import { EmergencyContactInput, Gender, StudentDto } from '../../api/models';
import { formatPhone } from '../../core/format/format';
import { interpolate, ui } from '../../core/i18n/tr';
import { ageOn, isMinor, todayIso } from '../../core/students/age';
import { optionalTurkishPhone } from '../../shared/forms/validators';

function trimmedRequired(control: AbstractControl): ValidationErrors | null {
  return String(control.value ?? '').trim() === '' ? { required: true } : null;
}

/** Student and emergency-contact controls shared by the register page and the edit dialog. */
export function studentFieldsForm(student?: StudentDto) {
  const contact = student?.emergencyContact;
  return new FormGroup({
    firstName: new FormControl(student?.firstName ?? '', {
      nonNullable: true,
      validators: [trimmedRequired, Validators.maxLength(100)],
    }),
    lastName: new FormControl(student?.lastName ?? '', {
      nonNullable: true,
      validators: [trimmedRequired, Validators.maxLength(100)],
    }),
    birthDate: new FormControl(student?.birthDate ?? '', {
      nonNullable: true,
      validators: [Validators.required],
    }),
    gender: new FormControl<Gender | ''>(student?.gender ?? '', {
      nonNullable: true,
      validators: [Validators.required],
    }),
    phone: new FormControl(student?.phone ? formatPhone(student.phone) : '', {
      nonNullable: true,
      validators: [optionalTurkishPhone],
    }),
    email: new FormControl(student?.email ?? '', {
      nonNullable: true,
      validators: [Validators.email, Validators.maxLength(254)],
    }),
    emergencyName: new FormControl(contact?.name ?? '', {
      nonNullable: true,
      validators: [trimmedRequired, Validators.maxLength(100)],
    }),
    emergencyPhone: new FormControl(contact?.phone ? formatPhone(contact.phone) : '', {
      nonNullable: true,
      validators: [trimmedRequired, optionalTurkishPhone],
    }),
    emergencyRelationship: new FormControl(contact?.relationship ?? '', {
      nonNullable: true,
      validators: [trimmedRequired, Validators.maxLength(50)],
    }),
  });
}

export type StudentFieldsForm = ReturnType<typeof studentFieldsForm>;
export type StudentField = keyof StudentFieldsForm['controls'];

/** API path of each control (the emergency contact is nested). */
export const studentApiPaths: Record<StudentField, string> = {
  firstName: 'firstName',
  lastName: 'lastName',
  birthDate: 'birthDate',
  gender: 'gender',
  phone: 'phone',
  email: 'email',
  emergencyName: 'emergencyContact.name',
  emergencyPhone: 'emergencyContact.phone',
  emergencyRelationship: 'emergencyContact.relationship',
};

/** Age from the browser date; the API decides with the school's time zone. */
export function ageInfo(birthDate: string): { age: number; minor: boolean; hint: string } | null {
  const age = ageOn(birthDate, todayIso());
  if (age === null) {
    return null;
  }
  const minor = isMinor(age);
  return {
    age,
    minor,
    hint: interpolate(minor ? ui.studentForm.minorHint : ui.studentForm.adultHint, { age }),
  };
}

export function studentBody(form: StudentFieldsForm) {
  const v = form.getRawValue();
  const optional = (value: string) => (value.trim() === '' ? null : value.trim());
  const emergencyContact: EmergencyContactInput = {
    name: v.emergencyName.trim(),
    phone: v.emergencyPhone.trim(),
    relationship: v.emergencyRelationship.trim(),
  };
  return {
    firstName: v.firstName.trim(),
    lastName: v.lastName.trim(),
    birthDate: v.birthDate,
    gender: v.gender as Gender,
    phone: optional(v.phone),
    email: optional(v.email),
    emergencyContact,
  };
}

/** Problems the API reports by code, placed on a field or section path. */
export const studentCodePaths: Readonly<Record<string, string>> = {
  'Student.GuardianRequired': 'guardians',
  'Student.PhoneRequiredForAdult': 'phone',
  'Student.EmergencyContactRequired': 'emergencyContact',
  'Guardian.PhoneTaken': 'guardians',
  'Guardian.NotFound': 'guardians',
};
