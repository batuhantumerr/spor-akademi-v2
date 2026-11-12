import { RouterProvider, type createBrowserRouter } from 'react-router';
import { useLanguage } from './language';

/**
 * P3-10 (DEC-426): a language switch re-mounts the routed tree, so every component reads the new text. The session
 * lives outside React and the query cache sits above this point, so cached answers show at once; only unsaved form
 * input and open notices are lost.
 */
export function LocalizedRouter({ router }: { router: ReturnType<typeof createBrowserRouter> }) {
  const language = useLanguage();
  return <RouterProvider key={language} router={router} />;
}
