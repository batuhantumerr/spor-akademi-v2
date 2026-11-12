import { Injectable, effect, inject, untracked } from '@angular/core';
import { Title } from '@angular/platform-browser';
import {
  ActivatedRouteSnapshot,
  PRIMARY_OUTLET,
  RouterStateSnapshot,
  TitleStrategy,
} from '@angular/router';

import { language } from './language';
import { ui } from './tr';

/**
 * Page titles read "<route title> · <app name>". Route titles are getters (`title: () => ui.nav.groups`), so the
 * strategy calls them again when the language changes and the tab title follows without a navigation (DEC-426).
 */
@Injectable()
export class AppTitleStrategy extends TitleStrategy {
  private readonly title = inject(Title);
  private last: RouterStateSnapshot | null = null;

  constructor() {
    super();
    effect(() => {
      language();
      untracked(() => {
        if (this.last) this.apply(this.last);
      });
    });
  }

  override updateTitle(snapshot: RouterStateSnapshot): void {
    this.last = snapshot;
    this.apply(snapshot);
  }

  private apply(snapshot: RouterStateSnapshot): void {
    const page = this.pageTitle(snapshot) ?? this.buildTitle(snapshot);
    this.title.setTitle(page ? `${page} · ${ui.appName}` : ui.appName);
  }

  /** The deepest primary route's title, re-read from its getter; an arrow function has no prototype. */
  private pageTitle(snapshot: RouterStateSnapshot): string | undefined {
    let page: string | undefined;
    let route: ActivatedRouteSnapshot | undefined = snapshot.root;
    while (route) {
      const title: unknown = route.routeConfig?.title;
      if (typeof title === 'string') {
        page = title;
      } else if (typeof title === 'function' && title.prototype === undefined) {
        page = (title as () => string)();
      }
      route = route.children.find((child) => child.outlet === PRIMARY_OUTLET);
    }
    return page;
  }
}
