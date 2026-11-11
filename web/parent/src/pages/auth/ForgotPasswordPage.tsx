import { Mail } from 'lucide-react';
import { useRef, useState, type SubmitEvent } from 'react';
import { Link } from 'react-router';
import { api } from '../../api/client';
import { errorMessage, unwrap } from '../../api/errors';
import { errorMessages, text } from '../../i18n/tr';
import { Notice, PublicLayout } from '../../layout/PublicLayout';
import { useDocumentTitle } from '../use-document-title';

/**
 * P2-F4 self-service reset for parents. The API always answers 202 and never says whether the address is
 * registered; the page says the same either way.
 */
export function ForgotPasswordPage() {
  useDocumentTitle(text.forgotPassword.title);
  const [email, setEmail] = useState('');
  const [touched, setTouched] = useState(false);
  const [submitting, setSubmitting] = useState(false);
  const [sent, setSent] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const emailRef = useRef<HTMLInputElement>(null);
  const emailError = touched && !email.trim() ? errorMessages['Field.Required'] : null;

  async function submit(event: SubmitEvent) {
    event.preventDefault();
    setTouched(true);
    if (!email.trim()) return emailRef.current?.focus();
    setSubmitting(true);
    setError(null);
    try {
      await unwrap(api.POST('/api/v1/auth/password-reset/request', { body: { email: email.trim() } }));
      setSent(true);
    } catch (failure) {
      setError(errorMessage(failure));
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <PublicLayout title={text.forgotPassword.title} icon={<Mail />}>
      <div role="status" aria-live="polite">
        {sent && <Notice tone="success">{text.forgotPassword.sent}</Notice>}
      </div>
      {!sent && (
        <>
          <p className="mb-4 text-sm text-muted-foreground">{text.forgotPassword.subtitle}</p>
          {error && (
            <div role="alert" className="mb-4">
              <Notice tone="error">{error}</Notice>
            </div>
          )}
          <form className="space-y-4" onSubmit={(event) => void submit(event)} noValidate>
            <div>
              <label className="field-label" htmlFor="email">
                {text.forgotPassword.email}
              </label>
              <input
                ref={emailRef}
                id="email"
                type="email"
                className="input"
                autoComplete="email"
                inputMode="email"
                value={email}
                onChange={(event) => {
                  setEmail(event.target.value);
                }}
                aria-invalid={emailError ? true : undefined}
                aria-describedby={emailError ? 'email-error' : undefined}
              />
              {emailError && (
                <p id="email-error" className="field-error">
                  {emailError}
                </p>
              )}
            </div>
            <button type="submit" className="btn btn-primary w-full" disabled={submitting}>
              {submitting ? text.forgotPassword.submitting : text.forgotPassword.submit}
            </button>
          </form>
        </>
      )}
      <Link
        to="/login"
        className="mt-5 inline-block text-sm font-medium text-primary underline-offset-4 hover:underline"
      >
        {text.forgotPassword.backToLogin}
      </Link>
    </PublicLayout>
  );
}
