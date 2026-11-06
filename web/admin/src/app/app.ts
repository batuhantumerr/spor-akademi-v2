import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterOutlet } from '@angular/router';

import { ToastsComponent } from './shared/ui/toast.service';

@Component({
  selector: 'app-root',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterOutlet, ToastsComponent],
  template: '<router-outlet /><app-toasts />',
})
export class App {}
