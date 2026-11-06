import { DIALOG_DATA, Dialog, DialogRef } from '@angular/cdk/dialog';
import { ChangeDetectionStrategy, Component, Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';

import { ui } from '../../core/i18n/tr';

export interface ConfirmOptions {
  title: string;
  message: string;
  confirmLabel: string;
  destructive?: boolean;
}

interface ConfirmData extends ConfirmOptions {
  titleId: string;
  messageId: string;
}

let nextId = 0;

@Component({
  selector: 'app-confirm-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div
      class="w-[min(28rem,calc(100vw-2rem))] rounded-lg border border-border bg-background p-6 shadow-lg"
    >
      <h2 class="text-lg font-semibold" [id]="data.titleId">{{ data.title }}</h2>
      <p class="mt-2 text-sm text-muted-foreground" [id]="data.messageId">{{ data.message }}</p>
      <div class="mt-6 flex flex-wrap justify-end gap-2">
        <button type="button" class="btn btn-secondary" (click)="ref.close(false)">
          {{ cancel }}
        </button>
        <button
          type="button"
          class="btn"
          [class.btn-destructive]="data.destructive"
          [class.btn-primary]="!data.destructive"
          (click)="ref.close(true)"
        >
          {{ data.confirmLabel }}
        </button>
      </div>
    </div>
  `,
})
export class ConfirmDialogComponent {
  protected readonly data = inject<ConfirmData>(DIALOG_DATA);
  protected readonly ref = inject<DialogRef<boolean>>(DialogRef);
  protected readonly cancel = ui.actions.cancel;
}

/** Accessible confirmation (focus trap, Escape to cancel, named by its title) on the CDK dialog. */
@Injectable({ providedIn: 'root' })
export class ConfirmService {
  private readonly dialog = inject(Dialog);

  async confirm(options: ConfirmOptions): Promise<boolean> {
    const id = ++nextId;
    const data: ConfirmData = {
      ...options,
      titleId: `confirm-title-${id}`,
      messageId: `confirm-message-${id}`,
    };
    const ref = this.dialog.open<boolean, ConfirmData>(ConfirmDialogComponent, {
      data,
      role: 'alertdialog',
      ariaLabelledBy: data.titleId,
      ariaDescribedBy: data.messageId,
      backdropClass: ['bg-slate-900/60'],
    });
    return (await firstValueFrom(ref.closed)) === true;
  }
}
