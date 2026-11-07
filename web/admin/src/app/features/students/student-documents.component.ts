import { HttpClient, HttpEventType, HttpRequest } from '@angular/common/http';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  computed,
  effect,
  inject,
  input,
  signal,
  untracked,
} from '@angular/core';
import { LucideExternalLink, LucideTrash2, LucideUpload, LucideUser } from '@lucide/angular';
import { Subscription } from 'rxjs';

import { Api } from '../../api/api';
import {
  apiV1DocumentsIdDelete,
  apiV1DocumentsIdLinkGet,
  apiV1StudentsIdDocumentsGet,
  apiV1StudentsIdDocumentsPost,
  apiV1StudentsIdPhotoGet,
} from '../../api/functions';
import { DocumentType, StudentDocumentDto } from '../../api/models';
import { problemMessage, toProblem } from '../../core/api/problem';
import { absoluteTime } from '../../core/format/relative-time';
import { errorMessages, interpolate, ui } from '../../core/i18n/tr';
import { SchoolClock } from '../../core/time/school-clock';
import { ConfirmService } from '../../shared/ui/confirm-dialog.component';
import { ToastService } from '../../shared/ui/toast.service';
import { acceptFor, checkFile, formatSize } from './document-files';

/**
 * P2-F6: a student's health reports and photo. Files go through the API (which inspects and cleans them); opening
 * one asks the API for a 5-minute signed link (DEC-321, DEC-323). School Admin only; coaches never see this section.
 */
@Component({
  selector: 'app-student-documents',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [LucideExternalLink, LucideTrash2, LucideUpload, LucideUser],
  template: `
    <section class="card p-4 lg:col-span-2" aria-labelledby="documents-title">
      <h2 id="documents-title" class="mb-1 text-base font-semibold">{{ text.title }}</h2>
      <p class="mb-3 text-xs text-muted-foreground">{{ text.privacy }}</p>

      <div class="flex flex-col gap-4 sm:flex-row">
        <figure class="flex shrink-0 flex-col items-center gap-1">
          @if (photoUrl(); as url) {
            <img
              [src]="url"
              [alt]="text.photoAlt"
              class="size-24 rounded-lg border border-border object-cover"
              data-testid="student-photo"
              referrerpolicy="no-referrer"
            />
          } @else {
            <div
              class="flex size-24 items-center justify-center rounded-lg border border-dashed border-border text-muted-foreground"
              data-testid="no-photo"
            >
              <svg lucideUser aria-hidden="true"></svg>
              <span class="sr-only">{{ text.noPhoto }}</span>
            </div>
          }
          <figcaption class="text-xs text-muted-foreground">{{ text.photo }}</figcaption>
        </figure>

        <form
          class="min-w-0 flex-1 space-y-3"
          (submit)="$event.preventDefault(); upload()"
          novalidate
        >
          <fieldset>
            <legend class="field-label">{{ text.typeLabel }}</legend>
            <div class="flex flex-wrap gap-4 text-sm">
              @for (t of types; track t) {
                <label class="flex items-center gap-2">
                  <input
                    type="radio"
                    name="document-type"
                    [value]="t"
                    [checked]="type() === t"
                    [disabled]="uploading()"
                    (change)="selectType(t)"
                  />
                  {{ text.types[t] }}
                </label>
              }
            </div>
          </fieldset>
          <div>
            <label class="field-label" for="document-file">{{ text.fileLabel }}</label>
            <input
              id="document-file"
              type="file"
              class="input py-1.5"
              [attr.accept]="accept()"
              [disabled]="uploading()"
              [attr.aria-invalid]="!!error()"
              [attr.aria-describedby]="'document-hint' + (error() ? ' document-error' : '')"
              (change)="pick($event)"
            />
            <p id="document-hint" class="mt-1 text-xs text-muted-foreground">
              {{ text.hints[type()] }}
            </p>
            @if (error(); as message) {
              <p id="document-error" role="alert" class="field-error">{{ message }}</p>
            }
          </div>
          @if (uploading()) {
            <div class="space-y-1">
              <progress
                class="h-2 w-full"
                max="100"
                [value]="progress()"
                [attr.aria-label]="text.progress"
              ></progress>
              <p class="text-xs text-muted-foreground" aria-live="polite">{{ progressText() }}</p>
            </div>
          }
          <div class="flex justify-end">
            <button type="submit" class="btn btn-primary" [disabled]="uploading()">
              <svg lucideUpload></svg>{{ uploading() ? progressText() : text.upload }}
            </button>
          </div>
        </form>
      </div>

      <div class="mt-4 border-t border-border/60 pt-3">
        @if (listError(); as message) {
          <div
            role="alert"
            class="flex items-center justify-between gap-3 text-sm text-destructive"
          >
            <span>{{ message }}</span>
            <button type="button" class="btn btn-secondary btn-sm" (click)="load()">
              {{ common.retry }}
            </button>
          </div>
        } @else if (documents() === null) {
          <p role="status" class="text-sm text-muted-foreground">{{ common.loading }}</p>
        } @else if (documents()!.length === 0) {
          <p class="text-sm text-muted-foreground">{{ text.empty }}</p>
        } @else {
          <ul class="divide-y divide-border/60" data-testid="document-list">
            @for (d of documents(); track d.id) {
              <li class="flex items-center justify-between gap-3 py-2">
                <div class="min-w-0 text-sm">
                  <p class="truncate font-medium">
                    {{ d.fileName }}
                    <span class="badge badge-muted ml-1">{{ text.types[d.type] }}</span>
                  </p>
                  <p class="text-xs text-muted-foreground">{{ details(d) }}</p>
                </div>
                <div class="flex shrink-0 gap-1">
                  <button
                    type="button"
                    class="btn btn-ghost btn-sm btn-icon"
                    [attr.aria-label]="label(text.openLabel, d)"
                    [attr.title]="text.open"
                    [disabled]="busy()"
                    (click)="open(d)"
                  >
                    <svg lucideExternalLink></svg>
                  </button>
                  <button
                    type="button"
                    class="btn btn-ghost btn-sm btn-icon text-destructive"
                    [attr.aria-label]="label(text.removeLabel, d)"
                    [attr.title]="text.remove"
                    [disabled]="busy()"
                    (click)="remove(d)"
                  >
                    <svg lucideTrash2></svg>
                  </button>
                </div>
              </li>
            }
          </ul>
        }
      </div>
    </section>
  `,
})
export class StudentDocumentsComponent {
  private readonly api = inject(Api);
  private readonly http = inject(HttpClient);
  private readonly toasts = inject(ToastService);
  private readonly confirmer = inject(ConfirmService);
  private readonly clock = inject(SchoolClock);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  readonly studentId = input.required<string>();

  protected readonly text = ui.documents;
  protected readonly common = ui.common;
  protected readonly types: readonly DocumentType[] = ['HealthReport', 'Photo'];
  protected readonly type = signal<DocumentType>('HealthReport');
  protected readonly file = signal<File | null>(null);
  protected readonly error = signal<string | null>(null);
  protected readonly uploading = signal(false);
  protected readonly progress = signal(0);
  protected readonly busy = signal(false);
  protected readonly documents = signal<StudentDocumentDto[] | null>(null);
  protected readonly listError = signal<string | null>(null);
  protected readonly photoUrl = signal<string | null>(null);
  protected readonly accept = computed(() => acceptFor(this.type()));
  protected readonly progressText = computed(() =>
    interpolate(this.text.uploading, { percent: this.progress() }),
  );

  private loadSequence = 0;
  private upload$: Subscription | null = null;

  constructor() {
    inject(DestroyRef).onDestroy(() => this.upload$?.unsubscribe());
    effect(() => {
      this.studentId();
      untracked(() => {
        this.documents.set(null);
        this.photoUrl.set(null);
        void this.load();
      });
    });
  }

  protected details(d: StudentDocumentDto): string {
    return interpolate(this.text.details, {
      size: formatSize(d.sizeBytes),
      date: absoluteTime(d.uploadedAt, this.clock.timeZone()),
    });
  }

  protected label(template: string, d: StudentDocumentDto): string {
    return interpolate(template, { name: d.fileName });
  }

  protected selectType(type: DocumentType): void {
    this.type.set(type);
    void this.validate();
  }

  protected pick(event: Event): void {
    const input = event.target as HTMLInputElement;
    this.file.set(input.files?.[0] ?? null);
    void this.validate();
  }

  /** Loads the list, then the photo link (a fresh 5-minute link each time). Older responses are ignored. */
  async load(): Promise<void> {
    const request = ++this.loadSequence;
    const id = this.studentId();
    this.listError.set(null);
    try {
      const documents = await this.api.invoke(apiV1StudentsIdDocumentsGet, { id });
      if (request !== this.loadSequence) {
        return;
      }
      this.documents.set(documents);
      if (!documents.some((d) => d.type === 'Photo')) {
        this.photoUrl.set(null);
        return;
      }
      const link = await this.api.invoke(apiV1StudentsIdPhotoGet, { id });
      if (request === this.loadSequence) {
        this.photoUrl.set(link.url);
      }
    } catch (err) {
      if (request === this.loadSequence) {
        this.listError.set(problemMessage(toProblem(err)));
      }
    }
  }

  protected async upload(): Promise<void> {
    if (this.uploading()) {
      return;
    }
    const file = this.file();
    if (!file) {
      this.error.set(this.text.chooseFile);
      return;
    }
    if (!(await this.validate())) {
      return;
    }
    const form = new FormData();
    form.append('type', this.type());
    form.append('file', file, file.name);
    const url = apiV1StudentsIdDocumentsPost.PATH.replace(
      '{id}',
      encodeURIComponent(this.studentId()),
    );
    this.uploading.set(true);
    this.progress.set(0);
    this.upload$ = this.http
      .request(new HttpRequest('POST', url, form, { reportProgress: true }))
      .subscribe({
        next: (event) => {
          if (event.type === HttpEventType.UploadProgress && event.total) {
            this.progress.set(Math.round((100 * event.loaded) / event.total));
          } else if (event.type === HttpEventType.Response) {
            this.finishUpload();
            this.toasts.success(this.text.uploaded);
            void this.load();
          }
        },
        error: (err: unknown) => {
          this.finishUpload(false);
          this.error.set(problemMessage(toProblem(err)));
        },
      });
  }

  /** Opens a tab synchronously (so no popup blocker interferes), then points it at the signed link. */
  protected async open(d: StudentDocumentDto): Promise<void> {
    const tab = window.open('', '_blank');
    if (tab) {
      tab.opener = null;
    }
    this.busy.set(true);
    try {
      const link = await this.api.invoke(apiV1DocumentsIdLinkGet, { id: d.id });
      if (tab) {
        tab.location.href = link.url;
      }
    } catch (err) {
      tab?.close();
      this.toasts.error(problemMessage(toProblem(err)));
      await this.load();
    } finally {
      this.busy.set(false);
    }
  }

  protected async remove(d: StudentDocumentDto): Promise<void> {
    if (this.busy()) {
      return;
    }
    this.busy.set(true);
    try {
      const ok = await this.confirmer.confirm({
        title: this.text.removeTitle,
        message: interpolate(this.text.removeText, { name: d.fileName }),
        confirmLabel: this.text.remove,
        destructive: true,
      });
      if (!ok) {
        return;
      }
      await this.api.invoke(apiV1DocumentsIdDelete, { id: d.id });
      this.toasts.success(this.text.removed);
    } catch (err) {
      this.toasts.error(problemMessage(toProblem(err)));
    } finally {
      this.busy.set(false);
    }
    await this.load();
  }

  /** The same checks the API makes first (size, magic bytes, extension); the API checks everything again. */
  private async validate(): Promise<boolean> {
    const file = this.file();
    if (!file) {
      this.error.set(null);
      return false;
    }
    const code = await checkFile(this.type(), file);
    this.error.set(code ? errorMessages[code] : null);
    return code === null;
  }

  private finishUpload(clear = true): void {
    this.uploading.set(false);
    this.upload$ = null;
    if (clear) {
      this.file.set(null);
      this.error.set(null);
      const input = this.host.nativeElement.querySelector<HTMLInputElement>('#document-file');
      if (input) {
        input.value = '';
      }
    }
  }
}
