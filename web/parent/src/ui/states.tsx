import { CircleAlert } from 'lucide-react';
import type { ReactNode } from 'react';
import { errorMessage } from '../api/errors';
import { text } from '../i18n/tr';

/** A polite "loading" line; screen readers hear it once. */
export function Loading({ label = text.app.loading }: { label?: string }) {
  return (
    <p role="status" className="flex items-center gap-2 py-4 text-sm text-muted-foreground">
      <span
        aria-hidden="true"
        className="size-4 animate-spin rounded-full border-2 border-current border-r-transparent motion-reduce:animate-none"
      />
      {label}
    </p>
  );
}

/** A failed load with its translated message and, when it can help, a retry button. */
export function ErrorState({ error, onRetry }: { error: unknown; onRetry?: (() => void) | undefined }) {
  return (
    <div
      role="alert"
      className="flex flex-col items-start gap-3 rounded-lg border border-destructive/30 bg-destructive/10 p-4 text-sm"
    >
      <p className="flex items-start gap-2 text-destructive">
        <CircleAlert aria-hidden="true" className="mt-0.5 size-4 shrink-0" />
        <span>{errorMessage(error)}</span>
      </p>
      {onRetry && (
        <button type="button" className="btn btn-secondary btn-sm" onClick={onRetry}>
          {text.app.retry}
        </button>
      )}
    </div>
  );
}

export function Empty({ children }: { children: ReactNode }) {
  return <p className="py-2 text-sm text-muted-foreground">{children}</p>;
}

/** A titled card section of a page; `id` makes it a jump target. */
export function Section({
  id,
  title,
  action,
  children,
}: {
  id: string;
  title: string;
  action?: ReactNode;
  children: ReactNode;
}) {
  return (
    <section id={id} aria-labelledby={`${id}-title`} className="card scroll-mt-28 p-4 sm:p-5">
      <div className="mb-3 flex items-center justify-between gap-3">
        <h2 id={`${id}-title`} className="text-lg">
          {title}
        </h2>
        {action}
      </div>
      {children}
    </section>
  );
}
