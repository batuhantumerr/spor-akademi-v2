import { Bell } from 'lucide-react';
import { useToasts } from './toasts';

/**
 * Real-time notices of the signed-in shell (P3-08). The live region is always in the page so screen readers hear
 * each new toast once (`polite`, never interrupting). Toasts have no controls, take no focus and let taps through
 * (`pointer-events-none`); on phones they sit above the bottom tab bar.
 */
export function ToastRegion() {
  const items = useToasts();
  return (
    <div
      aria-live="polite"
      className="pointer-events-none fixed inset-x-0 bottom-[calc(4.5rem+env(safe-area-inset-bottom))] z-50 flex flex-col items-center gap-2 px-4 sm:top-16 sm:right-4 sm:bottom-auto sm:left-auto sm:items-end"
    >
      {items.map((toast) => (
        <p
          key={toast.id}
          className="card flex w-full max-w-sm items-start gap-2 px-3 py-2.5 text-sm shadow-lg"
        >
          <Bell aria-hidden="true" className="mt-0.5 size-4 shrink-0 text-primary" />
          <span>{toast.message}</span>
        </p>
      ))}
    </div>
  );
}
