import { useEffect, useRef, useState } from 'react';
import { useLocation } from 'react-router';

/** How long a page may take to set its final title (a child's name arrives with its data). */
const TITLE_SETTLE_MS = 3000;

/**
 * Route changes for screen reader and keyboard users (P3-11, DEC-438; WCAG 2.4.3, 4.1.3). After a navigation to
 * another path, focus moves to the new page's `<main>` (so the next Tab starts in the new content, not on the link
 * that was used) and the page title is announced through a polite live region; a title that changes while the page
 * loads its data is announced once it settles. The first render (page load) is left to the browser.
 */
export function RouteAnnouncer() {
  const { pathname } = useLocation();
  const previous = useRef<string | null>(null);
  const [message, setMessage] = useState('');

  useEffect(() => {
    const last = previous.current;
    previous.current = pathname;
    if (last === null || last === pathname) return;

    const main = document.querySelector<HTMLElement>('main');
    if (main) {
      if (!main.hasAttribute('tabindex')) main.setAttribute('tabindex', '-1');
      main.focus({ preventScroll: true });
    }
    setMessage(document.title);
    const title = document.querySelector('title');
    if (!title) return;
    const observer = new MutationObserver(() => {
      setMessage(document.title);
    });
    observer.observe(title, { childList: true, characterData: true, subtree: true });
    const stop = window.setTimeout(() => {
      observer.disconnect();
    }, TITLE_SETTLE_MS);
    return () => {
      observer.disconnect();
      window.clearTimeout(stop);
    };
  }, [pathname]);

  return (
    <p className="sr-only" aria-live="polite" aria-atomic="true" data-testid="route-announcer">
      {message}
    </p>
  );
}
