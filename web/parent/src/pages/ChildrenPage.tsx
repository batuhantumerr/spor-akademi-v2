import { text } from '../i18n/tr';
import { useDocumentTitle } from './use-document-title';

/** Placeholder home ("Çocuklarım"); the list comes from the parent API in P3-06. */
export function ChildrenPage() {
  useDocumentTitle(text.children.title);

  return (
    <section aria-labelledby="children-title" className="space-y-4">
      <h1 id="children-title">{text.children.title}</h1>
      <div className="card p-6 text-sm text-muted-foreground">{text.children.placeholder}</div>
    </section>
  );
}
