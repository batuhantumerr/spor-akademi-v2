import { useEffect } from 'react';
import { pageTitle } from '../i18n/tr';

export function useDocumentTitle(page: string): void {
  useEffect(() => {
    document.title = pageTitle(page);
  }, [page]);
}
