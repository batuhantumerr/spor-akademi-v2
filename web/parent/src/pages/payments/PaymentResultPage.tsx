import { CircleCheck, CircleX, Clock } from 'lucide-react';
import { Link, useSearchParams } from 'react-router';
import { text } from '../../i18n/tr';
import { resultStatus } from '../../parent/checkout';
import { PublicLayout } from '../../layout/PublicLayout';
import { useDocumentTitle } from '../use-document-title';

/**
 * Where the payer lands after the hosted checkout (DEC-348; the API redirects to `/odeme-sonucu` on the origin the
 * provider returned to). Public: it only shows the status the API put in the redirect. The authoritative result is
 * the dues list, which is fetched fresh when the parent goes back.
 */
export function PaymentResultPage() {
  useDocumentTitle(text.paymentResult['title'] ?? '');
  const [params] = useSearchParams();
  const status = resultStatus(params.get('durum'));
  const icon =
    status === 'Succeeded' ? (
      <CircleCheck className="text-success" />
    ) : status === 'Pending' ? (
      <Clock />
    ) : (
      <CircleX className="text-destructive" />
    );

  return (
    <PublicLayout title={text.paymentResult['title'] ?? ''} icon={icon}>
      <p role="status" data-testid="result-message" className="text-sm">
        {text.paymentResult[status]}
      </p>
      <Link to="/" className="btn btn-primary mt-6 w-full">
        {text.paymentResult['back']}
      </Link>
    </PublicLayout>
  );
}
