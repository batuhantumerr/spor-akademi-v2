import { Moon, Sun } from 'lucide-react';
import type { ReactNode } from 'react';
import { text } from '../i18n/tr';
import { useTheme } from '../theme/theme';
import { LanguageSwitcher } from '../ui/LanguageSwitcher';

/** Centered single-card layout of the pages that need no session (login, password, payment pages). */
export function PublicLayout({
  title,
  icon,
  children,
}: {
  title: string;
  icon?: ReactNode;
  children: ReactNode;
}) {
  const theme = useTheme();
  return (
    <div className="flex min-h-dvh flex-col">
      <header className="mx-auto flex h-14 w-full max-w-md items-center px-4">
        <span className="text-base font-semibold tracking-tight">
          {text.app.name}
          <span className="ml-2 text-xs font-medium text-muted-foreground">{text.app.tagline}</span>
        </span>
        <LanguageSwitcher className="ml-auto" />
        <button
          type="button"
          className="btn btn-ghost btn-icon"
          aria-label={text.theme.toggle}
          title={theme.dark ? text.theme.light : text.theme.dark}
          onClick={theme.toggle}
        >
          {theme.dark ? <Sun aria-hidden="true" /> : <Moon aria-hidden="true" />}
        </button>
      </header>
      <main id="main" className="flex flex-1 items-start justify-center px-4 pt-4 pb-10 sm:items-center">
        <div className="card w-full max-w-md p-6 shadow-sm sm:p-8">
          <div className="mb-5 flex items-center gap-3">
            {icon && (
              <div className="grid size-10 shrink-0 place-items-center rounded-lg border border-primary/20 bg-primary/10 text-primary [&_svg]:size-5">
                {icon}
              </div>
            )}
            <h1 className="text-xl font-bold">{title}</h1>
          </div>
          {children}
        </div>
      </main>
      {/* P4-06 (DEC-543): the privacy notice (KVKK md. 10), a static page served next to the app. */}
      <footer className="px-4 pb-6 text-center text-xs">
        <a
          href="/aydinlatma-metni.html"
          className="text-muted-foreground underline underline-offset-4 hover:text-foreground"
        >
          {text.app.privacyNotice}
        </a>
      </footer>
    </div>
  );
}

/** A form-level error or notice block. */
export function Notice({ tone, children }: { tone: 'error' | 'success' | 'info'; children: ReactNode }) {
  const tones = {
    error: 'border-destructive/30 bg-destructive/10 text-destructive',
    success: 'border-success/30 bg-success/10',
    info: 'border-primary/20 bg-primary/5',
  } as const;
  return <div className={`rounded-lg border p-3 text-sm ${tones[tone]}`}>{children}</div>;
}
