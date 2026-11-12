import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterOutlet } from '@angular/router';

import { RouteAnnouncerComponent } from './core/routing/route-announcer.component';
import { ToastsComponent } from './shared/ui/toast.service';

@Component({
  selector: 'app-root',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterOutlet, ToastsComponent, RouteAnnouncerComponent],
  template: '<router-outlet /><app-toasts /><app-route-announcer />',
})
export class App {}
