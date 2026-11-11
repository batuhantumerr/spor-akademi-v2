import { text } from '../i18n/tr';

/** Route error boundary: never shows the exception (it may carry personal data), only a generic text. */
export function ErrorPage() {
  return (
    <div role="alert" className="mx-auto max-w-3xl p-6">
      <div className="card p-6 text-sm">{text.errors.unexpected}</div>
    </div>
  );
}
