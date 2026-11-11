import { CreditCard } from 'lucide-react';
import { useState } from 'react';
import { useSearchParams } from 'react-router';
import { api } from '../../api/client';
import { callbackTarget, postToCallback } from '../../parent/checkout';
import { errorMessage, unwrap } from '../../api/errors';
import { text } from '../../i18n/tr';
import { Notice, PublicLayout } from '../../layout/PublicLayout';
import { useDocumentTitle } from '../use-document-title';

/**
 * DEC-347: the Fake provider's hosted checkout page (public, no sign-in), as in web/admin. The Fake provider's
 * checkout link is relative (`/sandbox-odeme?token=…`), so a payment the parent starts stays on the parent app's
 * origin. No card data, no money; the payer approves or declines. The API refuses it outside the sandbox.
 */
export function SandboxCheckoutPage() {
  useDocumentTitle(text.sandbox.title);
  const [params] = useSearchParams();
  const token = params.get('token') ?? '';
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function complete(succeed: boolean) {
    if (busy) return;
    setBusy(true);
    setError(null);
    try {
      const result = await unwrap(
        api.POST('/api/v1/sandbox-checkout/complete', { body: { token, succeed } }),
      );
      const callback = callbackTarget(result.callbackUrl);
      if (!callback) {
        setError(text.sandbox.badCallback);
        setBusy(false);
        return;
      }
      postToCallback(callback, token);
    } catch (failure) {
      setError(errorMessage(failure));
      setBusy(false);
    }
  }

  return (
    <PublicLayout title={text.sandbox.title} icon={<CreditCard />}>
      <p className="text-sm text-muted-foreground">{text.sandbox.description}</p>
      {!token ? (
        <div role="alert" className="mt-4">
          <Notice tone="error">{text.sandbox.missingToken}</Notice>
        </div>
      ) : (
        <>
          {error && (
            <div role="alert" className="mt-4">
              <Notice tone="error">{error}</Notice>
            </div>
          )}
          <div className="mt-6 flex flex-wrap gap-3">
            <button
              type="button"
              className="btn btn-primary"
              disabled={busy}
              onClick={() => void complete(true)}
            >
              {busy ? text.sandbox.working : text.sandbox.pay}
            </button>
            <button
              type="button"
              className="btn btn-secondary"
              disabled={busy}
              onClick={() => void complete(false)}
            >
              {text.sandbox.decline}
            </button>
          </div>
        </>
      )}
    </PublicLayout>
  );
}
