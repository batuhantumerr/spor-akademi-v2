import { DOCUMENT } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import {
  LucideChevronsLeft,
  LucideChevronsRight,
  LucideDynamicIcon,
  LucideKeyRound,
  LucideLogOut,
  LucideMenu,
  LucideMoon,
  LucideSun,
  LucideTrophy,
  LucideX,
} from '@lucide/angular';

import { AuthService } from '../core/auth/auth.service';
import { ui } from '../core/i18n/tr';
import { ThemeService } from '../core/theme/theme.service';
import { MENU_NAVIGATION_STATE } from '../core/routing/menu-navigation';
import { navigationFor } from './navigation';
import { LOCALE } from '../core/i18n/locale';

/** Tailwind's `lg` breakpoint: the sidebar is a fixed rail above it and a drawer below. */
const DESKTOP_QUERY = '(min-width: 64rem)';

/** App shell: collapsible sidebar (drawer below lg), sticky header with theme and account actions. */
@Component({
  selector: 'app-shell',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    RouterOutlet,
    RouterLink,
    RouterLinkActive,
    LucideDynamicIcon,
    LucideChevronsLeft,
    LucideChevronsRight,
    LucideKeyRound,
    LucideLogOut,
    LucideMenu,
    LucideMoon,
    LucideSun,
    LucideTrophy,
    LucideX,
  ],
  host: { '(document:keydown.escape)': 'closeMenu()' },
  template: `
    <div class="flex h-dvh overflow-hidden bg-background">
      @if (mobileOpen()) {
        <!-- Pointer-only backdrop: keyboard users close the drawer with Escape or the X button. -->
        <button
          type="button"
          tabindex="-1"
          aria-hidden="true"
          class="fixed inset-0 z-30 bg-black/50 lg:hidden"
          data-testid="drawer-backdrop"
          (click)="closeMenu()"
        ></button>
      }

      <aside
        id="app-sidebar"
        class="fixed inset-y-0 left-0 z-40 flex w-64 flex-col border-r border-border bg-sidebar transition-[width,transform] duration-300 lg:translate-x-0"
        [class.lg:w-20]="collapsed()"
        [class.-translate-x-full]="!mobileOpen()"
        [class.max-lg:invisible]="drawerHidden()"
        [attr.inert]="drawerHidden() ? '' : null"
        [attr.aria-label]="text.shell.mainMenu"
      >
        <div class="relative flex h-16 shrink-0 items-center gap-3 border-b border-border px-4">
          <div
            class="grid size-9 shrink-0 place-items-center rounded-lg border border-primary/20 bg-primary/10 text-primary"
          >
            <svg lucideTrophy class="size-5"></svg>
          </div>
          @if (!rail()) {
            <div class="min-w-0">
              <p class="truncate text-sm font-bold tracking-tight text-foreground">
                {{ text.appName }}
              </p>
              <p class="truncate text-xs text-muted-foreground">{{ roleLabel() }}</p>
            </div>
          }
          <button
            type="button"
            class="absolute top-5 -right-3 hidden size-6 place-items-center rounded-full border border-border bg-card text-muted-foreground shadow-sm hover:text-primary lg:grid"
            aria-controls="app-sidebar"
            [attr.aria-expanded]="!collapsed()"
            [attr.aria-label]="collapsed() ? text.shell.expand : text.shell.collapse"
            (click)="collapsed.set(!collapsed())"
          >
            @if (collapsed()) {
              <svg lucideChevronsRight class="size-3.5"></svg>
            } @else {
              <svg lucideChevronsLeft class="size-3.5"></svg>
            }
          </button>
          <button
            #closeButton
            type="button"
            class="btn btn-ghost btn-icon ml-auto lg:hidden"
            [attr.aria-label]="text.shell.closeMenu"
            (click)="closeMenu()"
          >
            <svg lucideX></svg>
          </button>
        </div>

        <nav class="flex-1 space-y-4 overflow-y-auto px-3 py-4">
          @for (section of sections(); track $index) {
            <div [class.border-t]="$index > 0" class="border-border/50 pt-3 first:pt-0">
              @if (section.title && !rail()) {
                <p
                  class="mb-2 px-3 text-xs font-bold tracking-widest text-muted-foreground uppercase"
                >
                  {{ section.title }}
                </p>
              }
              <ul class="space-y-1">
                @for (item of section.items; track item.path) {
                  <li>
                    <a
                      [routerLink]="item.path"
                      [state]="menuNavigation"
                      routerLinkActive="!border-primary/30 bg-gradient-to-r from-primary/20 to-primary/5 !text-primary shadow-lg shadow-primary/5"
                      ariaCurrentWhenActive="page"
                      class="flex h-10 items-center gap-2.5 rounded-lg border border-transparent px-3 text-sm font-medium text-muted-foreground transition-all duration-200 hover:bg-muted/60 hover:text-foreground"
                      [class.justify-center]="rail()"
                      [attr.aria-label]="rail() ? item.label : null"
                      [attr.title]="rail() ? item.label : null"
                      (click)="mobileOpen.set(false)"
                    >
                      <svg
                        [lucideIcon]="item.icon"
                        class="size-5 shrink-0"
                        aria-hidden="true"
                      ></svg>
                      @if (!rail()) {
                        <span class="truncate">{{ item.label }}</span>
                      }
                    </a>
                  </li>
                }
              </ul>
            </div>
          }
        </nav>
      </aside>

      <div
        class="flex min-w-0 flex-1 flex-col transition-[margin] duration-300"
        [class.lg:ml-64]="!collapsed()"
        [class.lg:ml-20]="collapsed()"
      >
        <header
          class="sticky top-0 z-20 flex h-16 shrink-0 items-center gap-2 border-b border-border/50 bg-card/80 px-2 backdrop-blur-xl sm:px-6"
        >
          <button
            #menuButton
            type="button"
            class="btn btn-ghost btn-icon lg:hidden"
            aria-controls="app-sidebar"
            [attr.aria-expanded]="mobileOpen()"
            [attr.aria-label]="text.shell.openMenu"
            (click)="openMenu()"
          >
            <svg lucideMenu></svg>
          </button>
          <div class="ml-auto flex items-center gap-1">
            <button
              type="button"
              class="btn btn-ghost btn-icon"
              [attr.aria-label]="text.theme.toggle"
              [attr.title]="theme.isDark() ? text.theme.light : text.theme.dark"
              (click)="theme.toggle()"
            >
              @if (theme.isDark()) {
                <svg lucideSun></svg>
              } @else {
                <svg lucideMoon></svg>
              }
            </button>
            <a
              routerLink="/change-password"
              class="btn btn-ghost btn-icon"
              [attr.aria-label]="text.shell.changePassword"
              [attr.title]="text.shell.changePassword"
            >
              <svg lucideKeyRound></svg>
            </a>
            <div class="mx-2 hidden h-8 w-px bg-border sm:block"></div>
            <div class="hidden items-center gap-2.5 sm:flex">
              <div
                class="grid size-9 place-items-center rounded-full bg-primary/10 text-xs font-bold text-primary"
                aria-hidden="true"
              >
                {{ initials() }}
              </div>
              <div class="leading-tight">
                <p class="text-sm font-semibold">{{ fullName() }}</p>
                <p class="text-xs text-muted-foreground">{{ auth.user()?.email }}</p>
              </div>
            </div>
            <button
              type="button"
              class="btn btn-ghost btn-icon"
              [attr.aria-label]="text.shell.logout"
              [attr.title]="text.shell.logout"
              (click)="logout()"
            >
              <svg lucideLogOut></svg>
            </button>
          </div>
        </header>

        <main class="flex-1 overflow-y-auto p-4 md:p-6">
          <div class="mx-auto w-full max-w-7xl">
            <router-outlet />
          </div>
        </main>
      </div>
    </div>
  `,
})
export class ShellComponent {
  protected readonly auth = inject(AuthService);
  protected readonly theme = inject(ThemeService);
  protected readonly text = ui;
  private readonly injector = inject(Injector);
  private readonly menuButton = viewChild.required<ElementRef<HTMLButtonElement>>('menuButton');
  private readonly closeButton = viewChild.required<ElementRef<HTMLButtonElement>>('closeButton');

  protected readonly isDesktop = signal(false);
  protected readonly collapsed = signal(false);
  protected readonly mobileOpen = signal(false);
  /** Makes a click on the open page's menu link recreate that page so its lists reload (#79). */
  protected readonly menuNavigation = MENU_NAVIGATION_STATE;
  /** Icon-only rail: desktop with the sidebar collapsed. The mobile drawer always shows labels. */
  protected readonly rail = computed(() => this.collapsed() && this.isDesktop());
  /** Below lg a closed drawer is off-screen, so it must also leave the tab order. */
  protected readonly drawerHidden = computed(() => !this.isDesktop() && !this.mobileOpen());
  protected readonly sections = computed(() => navigationFor(this.auth.role()));
  protected readonly roleLabel = computed(() => ui.roles[this.auth.role() ?? ''] ?? '');
  protected readonly fullName = computed(() => {
    const user = this.auth.user();
    return user ? `${user.firstName} ${user.lastName}` : '';
  });
  protected readonly initials = computed(() => {
    const user = this.auth.user();
    return user
      ? `${user.firstName.charAt(0)}${user.lastName.charAt(0)}`.toLocaleUpperCase(LOCALE)
      : '';
  });

  constructor() {
    const view = inject(DOCUMENT).defaultView;
    const media = typeof view?.matchMedia === 'function' ? view.matchMedia(DESKTOP_QUERY) : null;
    if (media) {
      this.isDesktop.set(media.matches);
      const onChange = (event: MediaQueryListEvent) => {
        this.isDesktop.set(event.matches);
        if (event.matches) {
          this.mobileOpen.set(false);
        }
      };
      media.addEventListener('change', onChange);
      inject(DestroyRef).onDestroy(() => media.removeEventListener('change', onChange));
    }
  }

  protected openMenu(): void {
    this.mobileOpen.set(true);
    // The drawer is inert until this change renders; move focus once it is interactive.
    afterNextRender(() => this.closeButton().nativeElement.focus(), { injector: this.injector });
  }

  protected closeMenu(): void {
    if (!this.mobileOpen()) {
      return;
    }
    this.mobileOpen.set(false);
    afterNextRender(() => this.menuButton().nativeElement.focus(), { injector: this.injector });
  }

  protected logout(): void {
    this.auth.logout().subscribe();
  }
}
