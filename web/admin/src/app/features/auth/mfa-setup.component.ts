import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  viewChild,
} from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { LucideCircleAlert, LucideCopy, LucideDownload } from '@lucide/angular';
import qrcode from 'qrcode-generator';
import { Observable } from 'rxjs';

import { ApiProblem, problemMessage, toProblem } from '../../core/api/problem';
import { MfaEnrollmentStart } from '../../core/auth/auth.service';
import { errorMessages, ui } from '../../core/i18n/tr';

/** Quiet zone around a QR code, in modules (ISO/IEC 18004 asks for four). */
const QUIET_ZONE = 4;

/**
 * The SVG path of a QR code for `text`: one unit square per dark module, so the picture is a single scalable
 * element built by Angular, never injected as markup.
 */
export function qrPath(text: string): { path: string; size: number } {
  const code = qrcode(0, 'M');
  code.addData(text);
  code.make();
  const count = code.getModuleCount();
  let path = '';
  for (let row = 0; row < count; row++) {
    for (let col = 0; col < count; col++) {
      if (code.isDark(row, col)) {
        path += `M${col + QUIET_ZONE} ${row + QUIET_ZONE}h1v1h-1z`;
      }
    }
  }
  return { path, size: count + QUIET_ZONE * 2 };
}

/** The setup key in groups of four, easier to type from a screen. */
export function groupedSecret(secret: string): string {
  return secret.replace(/(.{4})(?=.)/g, '$1 ');
}

/**
 * P5-01: sets up two-step verification. It asks `begin` for a secret and shows it as a QR code and as text, asks for
 * the code the app displays, hands it to `confirm`, and shows the recovery codes once, until the user says they saved
 * them. The login (before a session exists) and the security settings (with one) share it through those two functions.
 */
@Component({
  selector: 'app-mfa-setup',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule, LucideCircleAlert, LucideCopy, LucideDownload],
  template: `
    @if (error()) {
      <div
        role="alert"
        class="mb-4 flex items-start gap-2 rounded-lg border border-destructive/30 bg-destructive/10 p-3 text-sm text-destructive"
      >
        <svg lucideCircleAlert class="mt-0.5 size-4 shrink-0" aria-hidden="true"></svg>
        <span>{{ error() }}</span>
      </div>
    }

    @if (recoveryCodes(); as codes) {
      <section aria-labelledby="mfa-codes-title">
        <h2 id="mfa-codes-title" #codesTitle tabindex="-1" class="text-lg font-semibold">
          {{ text.codesTitle }}
        </h2>
        <p class="mt-2 text-sm text-muted-foreground">{{ text.codesIntro }}</p>
        <ul
          class="mt-4 grid grid-cols-1 gap-2 rounded-lg border border-border bg-muted/40 p-4 font-mono text-sm sm:grid-cols-2"
          [attr.aria-label]="text.codesList"
        >
          @for (code of codes; track code) {
            <li>{{ code }}</li>
          }
        </ul>
        <div class="mt-3 flex flex-wrap gap-2">
          <button type="button" class="btn btn-secondary" (click)="copyCodes(codes)">
            <svg lucideCopy aria-hidden="true"></svg>{{ text.codesCopy }}
          </button>
          <button type="button" class="btn btn-secondary" (click)="downloadCodes(codes)">
            <svg lucideDownload aria-hidden="true"></svg>{{ text.codesDownload }}
          </button>
        </div>
        <p class="mt-2 min-h-5 text-xs" role="status">
          @if (copied() === true) {
            <span class="text-success">{{ text.copied }}</span>
          } @else if (copied() === false) {
            <span class="text-destructive">{{ text.copyFailed }}</span>
          }
        </p>
        <label class="mt-4 flex items-start gap-2 text-sm" for="mfa-codes-saved">
          <input
            id="mfa-codes-saved"
            type="checkbox"
            class="mt-0.5 size-4"
            [checked]="saved()"
            (change)="saved.set(!saved())"
          />
          <span>{{ text.saved }}</span>
        </label>
        <button
          type="button"
          class="btn btn-primary mt-4 w-full"
          [disabled]="!saved()"
          (click)="finished.emit()"
        >
          {{ text.continue }}
        </button>
      </section>
    } @else if (secret(); as start) {
      <p class="text-sm text-muted-foreground">{{ text.intro }}</p>
      @if (qr(); as image) {
        <svg
          role="img"
          class="mx-auto mt-4 size-48 rounded-lg border border-border bg-white"
          [attr.viewBox]="'0 0 ' + image.size + ' ' + image.size"
          [attr.aria-label]="text.qrLabel"
          shape-rendering="crispEdges"
        >
          <rect width="100%" height="100%" fill="#ffffff" />
          <path [attr.d]="image.path" fill="#000000" />
        </svg>
      }
      <p class="mt-4 text-sm text-muted-foreground">{{ text.manual }}</p>
      <div class="mt-2 flex items-stretch gap-2">
        <label class="sr-only" for="mfa-secret">{{ text.secretLabel }}</label>
        <input
          id="mfa-secret"
          class="input min-w-0 flex-1 font-mono text-sm tracking-wider"
          readonly
          autocomplete="off"
          spellcheck="false"
          [value]="grouped()"
          (focus)="selectAll($event)"
        />
        <button type="button" class="btn btn-secondary shrink-0" (click)="copySecret(start.secret)">
          <svg lucideCopy aria-hidden="true"></svg>{{ text.copy }}
        </button>
      </div>
      <p class="mt-2 min-h-5 text-xs" role="status">
        @if (copied() === true) {
          <span class="text-success">{{ text.copied }}</span>
        } @else if (copied() === false) {
          <span class="text-destructive">{{ text.copyFailed }}</span>
        }
      </p>
      <form class="mt-2 space-y-3" [formGroup]="form" (ngSubmit)="submit()" novalidate>
        <div>
          <label class="field-label" for="mfa-setup-code">{{ text.codePrompt }}</label>
          <input
            id="mfa-setup-code"
            class="input font-mono text-lg tracking-widest"
            formControlName="code"
            inputmode="numeric"
            autocomplete="one-time-code"
            maxlength="12"
            [attr.aria-invalid]="showRequired()"
            [attr.aria-describedby]="showRequired() ? 'mfa-setup-code-error' : null"
          />
          @if (showRequired()) {
            <p id="mfa-setup-code-error" class="field-error">{{ messages['Field.Required'] }}</p>
          }
        </div>
        <button type="submit" class="btn btn-primary w-full" [disabled]="busy()">
          {{ busy() ? text.confirming : text.confirm }}
        </button>
      </form>
    } @else if (!error()) {
      <p role="status" class="text-sm text-muted-foreground">{{ text.loading }}</p>
    }
  `,
})
export class MfaSetupComponent {
  private readonly formBuilder = inject(FormBuilder);

  /** Asks the server for a new secret. */
  readonly begin = input.required<() => Observable<MfaEnrollmentStart>>();
  /** Confirms the setup with a code; emits the recovery codes. */
  readonly confirm = input.required<(code: string) => Observable<string[]>>();
  /** Every failure, so a host can react to the ones that end the flow (an expired sign-in step). */
  readonly failed = output<ApiProblem>();
  /** The user saw and saved the recovery codes. */
  readonly finished = output<void>();

  protected readonly text = ui.mfa.setup;
  protected readonly messages = errorMessages;
  protected readonly secret = signal<MfaEnrollmentStart | null>(null);
  protected readonly recoveryCodes = signal<string[] | null>(null);
  protected readonly error = signal<string | null>(null);
  protected readonly busy = signal(false);
  protected readonly copied = signal<boolean | null>(null);
  protected readonly saved = signal(false);
  protected readonly grouped = computed(() => groupedSecret(this.secret()?.secret ?? ''));
  protected readonly qr = computed(() => {
    const start = this.secret();
    return start ? qrPath(start.otpAuthUri) : null;
  });
  protected readonly form = this.formBuilder.nonNullable.group({
    code: ['', Validators.required],
  });
  private readonly codesTitle = viewChild<ElementRef<HTMLElement>>('codesTitle');
  private started = false;

  constructor() {
    // Starts once, when the host has supplied `begin`.
    effect(() => {
      const begin = this.begin();
      if (!this.started) {
        this.started = true;
        begin().subscribe({
          next: (start) => this.secret.set(start),
          error: (err: unknown) => this.fail(err),
        });
      }
    });
    // The codes appear in place of the form: move focus to their heading so a screen reader announces them.
    effect(() => this.codesTitle()?.nativeElement.focus());
  }

  protected showRequired(): boolean {
    const control = this.form.controls.code;
    return control.invalid && (control.touched || control.dirty);
  }

  protected submit(): void {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }
    this.busy.set(true);
    this.error.set(null);
    this.confirm()(this.form.getRawValue().code).subscribe({
      next: (codes) => {
        this.busy.set(false);
        this.copied.set(null);
        this.recoveryCodes.set(codes);
      },
      error: (err: unknown) => {
        this.busy.set(false);
        this.form.controls.code.reset('');
        this.fail(err);
      },
    });
  }

  protected selectAll(event: Event): void {
    (event.target as HTMLInputElement).select();
  }

  protected copySecret(secret: string): Promise<void> {
    return this.copy(secret);
  }

  protected copyCodes(codes: string[]): Promise<void> {
    return this.copy(codes.join('\n'));
  }

  protected downloadCodes(codes: string[]): void {
    const url = URL.createObjectURL(new Blob([codes.join('\n') + '\n'], { type: 'text/plain' }));
    const link = document.createElement('a');
    link.href = url;
    link.download = this.text.codesFile;
    link.click();
    URL.revokeObjectURL(url);
  }

  private async copy(value: string): Promise<void> {
    try {
      await navigator.clipboard.writeText(value);
      this.copied.set(true);
    } catch {
      this.copied.set(false);
    }
  }

  private fail(err: unknown): void {
    const problem = toProblem(err);
    this.error.set(problemMessage(problem));
    this.failed.emit(problem);
  }
}
