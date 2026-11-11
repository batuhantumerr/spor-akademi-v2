import { Link } from 'react-router';
import { text } from '../i18n/tr';
import { useDocumentTitle } from './use-document-title';

export function NotFoundPage({ message = text.notFound.description }: { message?: string }) {
  useDocumentTitle(text.notFound.title);

  return (
    <section aria-labelledby="not-found-title" className="card space-y-3 p-6 text-center">
      <p className="font-mono text-3xl font-semibold text-muted-foreground">404</p>
      <h1 id="not-found-title">{text.notFound.title}</h1>
      <p className="text-sm text-muted-foreground">{message}</p>
      <Link to="/" className="btn btn-primary">
        {text.notFound.backHome}
      </Link>
    </section>
  );
}
