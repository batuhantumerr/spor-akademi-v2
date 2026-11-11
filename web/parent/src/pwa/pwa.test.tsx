import { act, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { session } from '../auth/session';
import { text } from '../i18n/tr';
import { renderApp } from '../test/render-app';
import { InstallHint } from './notices';
import { INSTALL_HINT_STORAGE_KEY, Pwa, pwa } from './pwa';

/** A registration and container with just what Pwa uses. */
function fakeWorker(options: { controlled: boolean; waiting?: boolean }) {
  const registration = Object.assign(new EventTarget(), {
    waiting: options.waiting ? { postMessage: vi.fn() } : null,
    installing: null as (EventTarget & { state: string }) | null,
    update: vi.fn(() => Promise.resolve()),
  });
  const container = Object.assign(new EventTarget(), {
    controller: options.controlled ? {} : null,
  });
  return {
    registration,
    container,
    asRegistration: registration as unknown as ServiceWorkerRegistration,
    asContainer: container as unknown as ServiceWorkerContainer,
  };
}

describe('service worker updates (P3-07)', () => {
  it('does not call the first install an update', () => {
    const app = new Pwa(window, vi.fn());
    const worker = fakeWorker({ controlled: false, waiting: true });

    app.watch(worker.asRegistration, worker.asContainer);

    expect(app.getState().updateReady).toBe(false);
  });

  it('offers a waiting version found at start-up', () => {
    const app = new Pwa(window, vi.fn());
    const worker = fakeWorker({ controlled: true, waiting: true });

    app.watch(worker.asRegistration, worker.asContainer);

    expect(app.getState().updateReady).toBe(true);
  });

  it('offers a version once it has installed in the background', () => {
    const app = new Pwa(window, vi.fn());
    const worker = fakeWorker({ controlled: true });
    app.watch(worker.asRegistration, worker.asContainer);
    const installing = Object.assign(new EventTarget(), { state: 'installing' });
    worker.registration.installing = installing;

    worker.registration.dispatchEvent(new Event('updatefound'));
    expect(app.getState().updateReady).toBe(false);
    installing.state = 'installed';
    installing.dispatchEvent(new Event('statechange'));

    expect(app.getState().updateReady).toBe(true);
  });

  it('lets the waiting version take over on "Yenile" and reloads once it controls the page', () => {
    const reload = vi.fn();
    const worker = fakeWorker({ controlled: true, waiting: true });
    const win = Object.assign(new EventTarget(), {
      navigator: { serviceWorker: worker.container },
      document: new EventTarget(),
    }) as unknown as Window;
    const app = new Pwa(win, reload);
    app.watch(worker.asRegistration, worker.asContainer);

    app.applyUpdate();

    expect(worker.registration.waiting!.postMessage).toHaveBeenCalledWith({ type: 'SKIP_WAITING' });
    expect(reload).not.toHaveBeenCalled();
    worker.container.dispatchEvent(new Event('controllerchange'));
    expect(reload).toHaveBeenCalledTimes(1);
  });

  it('shows the update banner on every page and applies the update from it', async () => {
    const applyUpdate = vi.spyOn(pwa, 'applyUpdate').mockImplementation(() => undefined);
    vi.spyOn(pwa, 'getState').mockReturnValue({ updateReady: true, installable: false });
    renderApp('/login');

    expect(await screen.findByText(text.pwa.updateReady)).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: text.pwa.reload }));

    expect(applyUpdate).toHaveBeenCalledTimes(1);
  });
});

describe('connection state (P3-07)', () => {
  it('says so when the browser goes offline', async () => {
    renderApp('/login');
    expect(await screen.findByRole('heading', { name: text.login.title })).toBeInTheDocument();
    expect(screen.queryByText(text.pwa.offlineBanner)).not.toBeInTheDocument();

    vi.spyOn(navigator, 'onLine', 'get').mockReturnValue(false);
    act(() => {
      window.dispatchEvent(new Event('offline'));
    });

    expect(screen.getByText(text.pwa.offlineBanner)).toBeInTheDocument();
  });

  it('shows "Bağlantı yok" instead of the login form when the session could not be checked, and retries', async () => {
    vi.spyOn(session, 'getState').mockReturnValue({ status: 'offline', user: null, expired: false });
    const retry = vi.spyOn(session, 'retryRestore').mockResolvedValue();
    renderApp('/');

    expect(await screen.findByRole('heading', { level: 1, name: text.pwa.offlineTitle })).toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: text.login.title })).not.toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: text.app.retry }));
    expect(retry).toHaveBeenCalledTimes(1);
    act(() => {
      window.dispatchEvent(new Event('online'));
    });
    expect(retry).toHaveBeenCalledTimes(2);
  });
});

describe('install hint (P3-07)', () => {
  it('stays hidden until the browser offers installation', () => {
    render(<InstallHint />);
    expect(screen.queryByText(text.pwa.installHint)).not.toBeInTheDocument();
  });

  it('opens the browser dialog once and is not shown again after closing', async () => {
    vi.spyOn(pwa, 'getState').mockReturnValue({ updateReady: false, installable: true });
    const install = vi.spyOn(pwa, 'install').mockResolvedValue();
    const { unmount } = render(<InstallHint />);

    expect(screen.getByRole('complementary', { name: text.pwa.install })).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: text.pwa.install }));
    expect(install).toHaveBeenCalledTimes(1);
    expect(screen.queryByText(text.pwa.installHint)).not.toBeInTheDocument();
    expect(localStorage.getItem(INSTALL_HINT_STORAGE_KEY)).toBe('dismissed');

    unmount();
    render(<InstallHint />);
    expect(screen.queryByText(text.pwa.installHint)).not.toBeInTheDocument();
  });

  it('can be closed without installing', async () => {
    vi.spyOn(pwa, 'getState').mockReturnValue({ updateReady: false, installable: true });
    const install = vi.spyOn(pwa, 'install').mockResolvedValue();
    render(<InstallHint />);

    await userEvent.click(screen.getByRole('button', { name: text.pwa.dismissInstall }));

    expect(install).not.toHaveBeenCalled();
    expect(screen.queryByText(text.pwa.installHint)).not.toBeInTheDocument();
  });

  it('keeps the browser prompt for the hint and uses it once', async () => {
    const container = Object.assign(new EventTarget(), {
      controller: null,
      register: vi.fn(() => new Promise<never>(() => undefined)),
    });
    const win = Object.assign(new EventTarget(), {
      navigator: { serviceWorker: container },
      document: Object.assign(new EventTarget(), { readyState: 'complete', visibilityState: 'visible' }),
    }) as unknown as Window;
    const app = new Pwa(win, vi.fn());
    app.start();
    expect(container.register).toHaveBeenCalledWith('/sw.js', { scope: '/', updateViaCache: 'none' });

    const prompt = vi.fn(() => Promise.resolve());
    const event = Object.assign(new Event('beforeinstallprompt', { cancelable: true }), { prompt });
    win.dispatchEvent(event);
    expect(event.defaultPrevented).toBe(true);
    expect(app.getState().installable).toBe(true);

    await app.install();
    await app.install();
    expect(prompt).toHaveBeenCalledTimes(1);
    expect(app.getState().installable).toBe(false);
  });
});
