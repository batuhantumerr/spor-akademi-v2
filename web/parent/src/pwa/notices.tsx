import { Download, RefreshCw, WifiOff, X } from 'lucide-react';
import { useEffect, useState } from 'react';
import { Outlet } from 'react-router';
import { session } from '../auth/session';
import { text } from '../i18n/tr';
import { dismissInstallHint, installHintDismissed, pwa, useOnline, usePwa } from './pwa';

/**
 * Connection and version notices above every page (P3-07). Plain text bars in the page flow: they never cover
 * content, never animate, and screen readers hear them once (`role="status"`).
 */
export function RootLayout() {
  return (
    <>
      <ConnectionBanner />
      <UpdateBanner />
      <Outlet />
    </>
  );
}

function ConnectionBanner() {
  const online = useOnline();
  if (online) return null;
  return (
    <p
      role="status"
      className="flex items-center justify-center gap-2 border-b border-warning/30 bg-warning/10 px-4 py-2 text-center text-sm text-warning"
    >
      <WifiOff aria-hidden="true" className="size-4 shrink-0" />
      {text.pwa.offlineBanner}
    </p>
  );
}

function UpdateBanner() {
  const { updateReady } = usePwa();
  if (!updateReady) return null;
  return (
    <div
      role="status"
      className="flex items-center justify-center gap-3 border-b border-primary/30 bg-primary/10 px-4 py-2 text-sm"
    >
      <span>{text.pwa.updateReady}</span>
      <button
        type="button"
        className="btn btn-primary btn-sm"
        onClick={() => {
          pwa.applyUpdate();
        }}
      >
        <RefreshCw aria-hidden="true" className="size-4" />
        {text.pwa.reload}
      </button>
    </div>
  );
}

/**
 * The full page when the start-up session restore found no network: instead of a login form that cannot work,
 * "Bağlantı yok" with a retry. It retries by itself when the browser reports the connection is back. No personal
 * data is shown offline: the worker never caches API answers (DEC-394).
 */
export function OfflineState() {
  useEffect(() => {
    const retry = () => void session.retryRestore();
    window.addEventListener('online', retry);
    return () => {
      window.removeEventListener('online', retry);
    };
  }, []);

  return (
    <main id="main" className="flex min-h-dvh items-center justify-center p-4">
      <div className="card flex max-w-sm flex-col items-center gap-3 p-6 text-center">
        <WifiOff aria-hidden="true" className="size-8 text-muted-foreground" />
        <h1 className="text-xl">{text.pwa.offlineTitle}</h1>
        <p className="text-sm text-muted-foreground">{text.pwa.offlineBody}</p>
        <button
          type="button"
          className="btn btn-secondary"
          onClick={() => {
            void session.retryRestore();
          }}
        >
          {text.app.retry}
        </button>
      </div>
    </main>
  );
}

/**
 * "Ana ekrana ekle": shown only when the browser itself offers installation (Chromium's `beforeinstallprompt`), and
 * never again in this browser once closed. Safari and Firefox users are not prompted.
 */
export function InstallHint() {
  const { installable } = usePwa();
  const [dismissed, setDismissed] = useState(installHintDismissed);
  if (!installable || dismissed) return null;

  const dismiss = () => {
    dismissInstallHint();
    setDismissed(true);
  };

  return (
    <aside
      aria-label={text.pwa.install}
      className="card mb-4 flex items-start gap-3 p-3 text-sm sm:items-center"
    >
      <Download aria-hidden="true" className="mt-0.5 size-4 shrink-0 text-primary sm:mt-0" />
      <p className="flex-1">{text.pwa.installHint}</p>
      <button
        type="button"
        className="btn btn-secondary btn-sm"
        onClick={() => {
          dismiss();
          void pwa.install();
        }}
      >
        {text.pwa.install}
      </button>
      <button
        type="button"
        className="btn btn-ghost btn-icon btn-sm"
        aria-label={text.pwa.dismissInstall}
        title={text.pwa.dismissInstall}
        onClick={dismiss}
      >
        <X aria-hidden="true" className="size-4" />
      </button>
    </aside>
  );
}
