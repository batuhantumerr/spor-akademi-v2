import { KeyRound } from 'lucide-react';
import { useEffect, useRef, useState, type SubmitEvent } from 'react';
import { Link } from 'react-router';
import { api } from '../../api/client';
import { errorMessage, toApiError, unwrap } from '../../api/errors';
import { tokenFromFragment } from '../../auth/links';
import { session } from '../../auth/session';
import { errorMessages, text } from '../../i18n/tr';
import { Notice, PublicLayout } from '../../layout/PublicLayout';
import { useDocumentTitle } from '../use-document-title';

const MIN_LENGTH = 12;

/**
 * P3-05 invitation acceptance and password reset (DEC-383, DEC-351): both links open this page with a single-use
 * token in the URL fragment, which browsers never send to a server. The page reads it once and removes it from the
 * address bar and history at once.
 */
export function ResetPasswordPage() {
  useDocumentTitle(text.resetPassword.title);
  const [token] = useState(() => tokenFromFragment(window.location.hash));
  const [password, setPassword] = useState('');
  const [confirm, setConfirm] = useState('');
  const [touched, setTouched] = useState(false);
  const [submitting, setSubmitting] = useState(false);
  const [done, setDone] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [tokenRejected, setTokenRejected] = useState(false);
  const passwordRef = useRef<HTMLInputElement>(null);
  const confirmRef = useRef<HTMLInputElement>(null);

  useEffect(() => {
    // Keep the token out of the address bar, history and any later Referer (nginx also sends no-referrer).
    if (window.location.hash) {
      window.history.replaceState(window.history.state, '', window.location.pathname);
    }
  }, []);

  const passwordError = !touched
    ? null
    : !password
      ? errorMessages['Field.Required']
      : password.length < MIN_LENGTH
        ? errorMessages['Auth.PasswordTooWeak']
        : null;
  const confirmError = !touched
    ? null
    : !confirm
      ? errorMessages['Field.Required']
      : confirm !== password
        ? text.resetPassword.mismatch
        : null;

  async function submit(event: SubmitEvent) {
    event.preventDefault();
    setTouched(true);
    if (!password || password.length < MIN_LENGTH) return passwordRef.current?.focus();
    if (confirm !== password) return confirmRef.current?.focus();
    if (!token) return;
    setSubmitting(true);
    setError(null);
    try {
      await unwrap(
        api.POST('/api/v1/auth/password-reset/confirm', { body: { token, newPassword: password } }),
      );
      // Setting the password revoked every session (P2-F4); an open one in this tab is stale.
      session.forget();
      setDone(true);
    } catch (failure) {
      setTokenRejected(toApiError(failure).code === 'Auth.ResetTokenInvalid');
      setError(errorMessage(failure));
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <PublicLayout title={text.resetPassword.title} icon={<KeyRound />}>
      <div role="status" aria-live="polite">
        {done && <Notice tone="success">{text.resetPassword.success}</Notice>}
      </div>
      {done ? (
        <Link to="/login" className="btn btn-primary mt-6 w-full">
          {text.resetPassword.toLogin}
        </Link>
      ) : !token ? (
        <>
          <div role="alert">
            <Notice tone="error">{text.resetPassword.missingToken}</Notice>
          </div>
          <Link to="/forgot-password" className="btn btn-primary mt-6 w-full">
            {text.resetPassword.requestNew}
          </Link>
        </>
      ) : (
        <>
          <p className="mb-4 text-sm text-muted-foreground">{text.resetPassword.subtitle}</p>
          {error && (
            <div role="alert" className="mb-4">
              <Notice tone="error">
                {error}{' '}
                {tokenRejected && (
                  <Link to="/forgot-password" className="font-medium underline">
                    {text.resetPassword.requestNew}
                  </Link>
                )}
              </Notice>
            </div>
          )}
          <form className="space-y-4" onSubmit={(event) => void submit(event)} noValidate>
            <div>
              <label className="field-label" htmlFor="new-password">
                {text.resetPassword.next}
              </label>
              <input
                ref={passwordRef}
                id="new-password"
                type="password"
                className="input"
                autoComplete="new-password"
                value={password}
                onChange={(event) => {
                  setPassword(event.target.value);
                }}
                aria-invalid={passwordError ? true : undefined}
                aria-describedby={passwordError ? 'new-hint new-error' : 'new-hint'}
              />
              <p id="new-hint" className="mt-1.5 text-xs text-muted-foreground">
                {text.resetPassword.hint}
              </p>
              {passwordError && (
                <p id="new-error" className="field-error">
                  {passwordError}
                </p>
              )}
            </div>
            <div>
              <label className="field-label" htmlFor="confirm-password">
                {text.resetPassword.confirm}
              </label>
              <input
                ref={confirmRef}
                id="confirm-password"
                type="password"
                className="input"
                autoComplete="new-password"
                value={confirm}
                onChange={(event) => {
                  setConfirm(event.target.value);
                }}
                aria-invalid={confirmError ? true : undefined}
                aria-describedby={confirmError ? 'confirm-error' : undefined}
              />
              {confirmError && (
                <p id="confirm-error" className="field-error">
                  {confirmError}
                </p>
              )}
            </div>
            <button type="submit" className="btn btn-primary w-full" disabled={submitting}>
              {submitting ? text.resetPassword.submitting : text.resetPassword.submit}
            </button>
          </form>
        </>
      )}
    </PublicLayout>
  );
}
