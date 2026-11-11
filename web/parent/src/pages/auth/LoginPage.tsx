import { Eye, EyeOff, LogIn } from 'lucide-react';
import { useRef, useState, type SubmitEvent } from 'react';
import { Link, useSearchParams } from 'react-router';
import { errorMessage } from '../../api/errors';
import { session } from '../../auth/session';
import { errorMessages, text } from '../../i18n/tr';
import { Notice, PublicLayout } from '../../layout/PublicLayout';
import { useDocumentTitle } from '../use-document-title';

/**
 * P3-05 parent sign-in. A successful login switches the session to signed-in; the guest guard then sends the
 * parent to the page they came from (`returnUrl`) or home.
 */
export function LoginPage() {
  useDocumentTitle(text.login.title);
  const [params] = useSearchParams();
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [showPassword, setShowPassword] = useState(false);
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [touched, setTouched] = useState(false);
  const emailRef = useRef<HTMLInputElement>(null);
  const passwordRef = useRef<HTMLInputElement>(null);

  const emailError = touched && !email.trim() ? errorMessages['Field.Required'] : null;
  const passwordError = touched && !password ? errorMessages['Field.Required'] : null;

  async function submit(event: SubmitEvent) {
    event.preventDefault();
    setTouched(true);
    if (!email.trim()) return emailRef.current?.focus();
    if (!password) return passwordRef.current?.focus();
    setSubmitting(true);
    setError(null);
    try {
      await session.login(email.trim(), password);
    } catch (failure) {
      setError(errorMessage(failure));
      setPassword('');
      setSubmitting(false);
      passwordRef.current?.focus();
    }
  }

  return (
    <PublicLayout title={text.login.title} icon={<LogIn />}>
      <p className="mb-4 text-sm text-muted-foreground">{text.login.subtitle}</p>
      <div aria-live="polite" className="mb-4 space-y-3 empty:mb-0">
        {params.get('expired') === '1' && !error && <Notice tone="info">{text.login.expired}</Notice>}
        {error && (
          <div role="alert">
            <Notice tone="error">{error}</Notice>
          </div>
        )}
      </div>
      <form className="space-y-4" onSubmit={(event) => void submit(event)} noValidate>
        <div>
          <label className="field-label" htmlFor="email">
            {text.login.email}
          </label>
          <input
            ref={emailRef}
            id="email"
            type="email"
            className="input"
            autoComplete="username"
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
        <div>
          <label className="field-label" htmlFor="password">
            {text.login.password}
          </label>
          <div className="relative">
            <input
              ref={passwordRef}
              id="password"
              type={showPassword ? 'text' : 'password'}
              className="input pr-11"
              autoComplete="current-password"
              value={password}
              onChange={(event) => {
                setPassword(event.target.value);
              }}
              aria-invalid={passwordError ? true : undefined}
              aria-describedby={passwordError ? 'password-error' : undefined}
            />
            <button
              type="button"
              className="btn btn-ghost btn-icon absolute top-0 right-0 h-10"
              aria-label={showPassword ? text.login.hidePassword : text.login.showPassword}
              aria-pressed={showPassword}
              onClick={() => {
                setShowPassword((shown) => !shown);
              }}
            >
              {showPassword ? <EyeOff aria-hidden="true" /> : <Eye aria-hidden="true" />}
            </button>
          </div>
          {passwordError && (
            <p id="password-error" className="field-error">
              {passwordError}
            </p>
          )}
        </div>
        <button type="submit" className="btn btn-primary w-full" disabled={submitting}>
          {submitting ? text.login.submitting : text.login.submit}
        </button>
      </form>
      <div className="mt-5 space-y-2 text-sm">
        <Link to="/forgot-password" className="font-medium text-primary underline-offset-4 hover:underline">
          {text.login.forgotPassword}
        </Link>
        <p className="text-muted-foreground">{text.login.invitationHint}</p>
      </div>
    </PublicLayout>
  );
}
