import { Api } from '../../api/api';
import { apiV1PaymentsIdRefundCheckPost } from '../../api/functions';
import { problemMessage, toProblem } from '../../core/api/problem';
import { ui } from '../../core/i18n/tr';
import { ToastService } from '../../shared/ui/toast.service';

/**
 * P3-13 (DEC-418, DEC-419): "Sağlayıcıda kontrol et". Settles a pending refund from the provider's payment detail and
 * tells the admin what happened; the caller reloads its list either way.
 */
export async function checkRefundAtProvider(
  api: Api,
  toasts: ToastService,
  paymentId: string,
): Promise<void> {
  const text = ui.billing;
  try {
    const payment = await api.invoke(apiV1PaymentsIdRefundCheckPost, { id: paymentId });
    if (payment.status === 'Refunded') {
      toasts.success(text.refundCheckSucceeded);
    } else {
      toasts.error(text.refundCheckPending);
    }
  } catch (err) {
    const problem = toProblem(err);
    if (problem.code === 'Refund.ProviderDeclined') {
      toasts.error(text.refundCheckNotRefunded);
    } else if (problem.code === 'Refund.InProgress') {
      toasts.error(text.refundCheckTooSoon);
    } else {
      toasts.error(problemMessage(problem));
    }
  }
}
