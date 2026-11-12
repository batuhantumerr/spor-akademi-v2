import { useEffect, useRef } from 'react';
import { RouterProvider, type createBrowserRouter } from 'react-router';
import { useLanguage } from './language';

/**
 * P3-10 (DEC-426): a language switch re-mounts the routed tree, so every component reads the new text. The session
 * lives outside React and the query cache sits above this point, so cached answers show at once; only unsaved form
 * input and open notices are lost. The re-mount would drop keyboard focus to the page start, so focus goes back to the
 * language switcher that was used (P3-11, DEC-439; WCAG 2.4.3).
 */
export function LocalizedRouter({ router }: { router: ReturnType<typeof createBrowserRouter> }) {
  const language = useLanguage();
  const previous = useRef(language);

  useEffect(() => {
    if (previous.current === language) return;
    previous.current = language;
    document.querySelector<HTMLElement>('[data-testid="language-switcher"]')?.focus();
  }, [language]);

  return <RouterProvider key={language} router={router} />;
}
