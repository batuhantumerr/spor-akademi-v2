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

@Component({
  selector: 'app-confirm-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div
      class="w-[min(28rem,calc(100vw-2rem))] rounded-lg border border-border bg-background p-6 shadow-lg"
    >
      <h2 class="text-lg font-semibold">{{ data.title }}</h2>
      <p class="mt-2 text-sm text-muted-foreground">{{ data.message }}</p>
      <div class="mt-6 flex justify-end gap-2">
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
  protected readonly data = inject<ConfirmOptions>(DIALOG_DATA);
  protected readonly ref = inject<DialogRef<boolean>>(DialogRef);
  protected readonly cancel = ui.common.cancel;
}

/** Accessible confirmation (focus trap, Escape to cancel) on top of the CDK dialog. */
@Injectable({ providedIn: 'root' })
export class ConfirmService {
  private readonly dialog = inject(Dialog);

  async confirm(options: ConfirmOptions): Promise<boolean> {
    const ref = this.dialog.open<boolean, ConfirmOptions>(ConfirmDialogComponent, {
      data: options,
      backdropClass: ['bg-slate-900/60'],
    });
    return (await firstValueFrom(ref.closed)) === true;
  }
}
