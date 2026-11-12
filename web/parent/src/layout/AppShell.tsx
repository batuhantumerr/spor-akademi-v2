import { LogOut, Moon, Sun, Users } from 'lucide-react';
import { useState } from 'react';
import { NavLink, Outlet } from 'react-router';
import { session } from '../auth/session';
import { text } from '../i18n/tr';
import { InstallHint } from '../pwa/notices';
import { ToastRegion } from '../realtime/ToastRegion';
import { useTheme } from '../theme/theme';

const navItems = [{ to: '/', label: text.nav.children, icon: Users, end: true }] as const;

/**
 * Mobile-first shell: sticky header with the theme toggle, content column, and a bottom tab bar on
 * phones that becomes a header nav from `sm` up. Sign-out ends the session here first, then on the server; the
 * session guard then shows the login page.
 */
export function AppShell() {
  const theme = useTheme();
  const [signingOut, setSigningOut] = useState(false);

  return (
    <div className="flex min-h-dvh flex-col">
      <a
        href="#main"
        className="sr-only rounded-md bg-primary px-4 py-2 text-primary-foreground focus:not-sr-only focus:absolute focus:top-2 focus:left-2 focus:z-50"
      >
        {text.app.skipToContent}
      </a>

      <header className="sticky top-0 z-40 border-b border-border bg-card/95 backdrop-blur supports-[backdrop-filter]:bg-card/80">
        <div className="mx-auto flex h-14 w-full max-w-3xl items-center gap-3 px-4">
          <span className="text-base font-semibold tracking-tight">
            {text.app.name}
            <span className="ml-2 text-xs font-medium text-muted-foreground">{text.app.tagline}</span>
          </span>

          <nav aria-label={text.app.mainNavigation} className="ml-auto hidden sm:block">
            <ul className="flex items-center gap-1">
              {navItems.map((item) => (
                <li key={item.to}>
                  <NavLink to={item.to} end={item.end} className={navLinkClass}>
                    {item.label}
                  </NavLink>
                </li>
              ))}
            </ul>
          </nav>

          <button
            type="button"
            className="btn btn-ghost btn-icon ml-auto sm:ml-0"
            aria-label={text.theme.toggle}
            title={theme.dark ? text.theme.light : text.theme.dark}
            onClick={theme.toggle}
          >
            {theme.dark ? <Sun aria-hidden="true" /> : <Moon aria-hidden="true" />}
          </button>
          <button
            type="button"
            className="btn btn-ghost btn-icon"
            aria-label={text.nav.logout}
            title={text.nav.logout}
            disabled={signingOut}
            onClick={() => {
              setSigningOut(true);
              void session.logout();
            }}
          >
            <LogOut aria-hidden="true" />
          </button>
        </div>
      </header>

      <main id="main" tabIndex={-1} className="mx-auto w-full max-w-3xl flex-1 px-4 pt-6 pb-24 sm:pb-10">
        <InstallHint />
        <Outlet />
      </main>

      <nav
        aria-label={text.app.mainNavigation}
        className="fixed inset-x-0 bottom-0 z-40 border-t border-border bg-card pb-[env(safe-area-inset-bottom)] sm:hidden"
      >
        <ul className="mx-auto flex max-w-3xl">
          {navItems.map((item) => (
            <li key={item.to} className="flex-1">
              <NavLink to={item.to} end={item.end} className={tabLinkClass}>
                <item.icon aria-hidden="true" className="size-5" />
                <span>{item.label}</span>
              </NavLink>
            </li>
          ))}
        </ul>
      </nav>

      {/* P3-08: real-time notices (attendance, payments, announcements) on every signed-in page. */}
      <ToastRegion />
    </div>
  );
}

function navLinkClass({ isActive }: { isActive: boolean }): string {
  return `btn btn-sm ${isActive ? 'bg-primary/10 text-primary' : 'btn-ghost'}`;
}

function tabLinkClass({ isActive }: { isActive: boolean }): string {
  return `flex min-h-14 flex-col items-center justify-center gap-0.5 rounded-md text-xs font-medium focus-visible:ring-2 focus-visible:ring-primary/50 focus-visible:outline-none focus-visible:ring-inset ${
    isActive ? 'text-primary' : 'text-muted-foreground'
  }`;
}
