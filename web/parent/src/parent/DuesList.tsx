import { CreditCard } from 'lucide-react';
import { useEffect, useRef, useState } from 'react';
import { errorMessage } from '../api/errors';
import { formatMoney, formatPeriod } from '../format/format';
import { format, text } from '../i18n/tr';
import { owedTotal } from './summary';
import { browser, checkoutTarget } from './checkout';
import { startOnlinePayment, type Due } from './queries';

const dueKey = (due: Due) => `${due.enrollmentId}|${due.period}`;

/**
 * The child's unpaid dues (the same rows as the School Admin's list, DEC-385) with the list fee and the reason of a
 * discount, and an online payment per row (DEC-386). P4-10: the pay buttons need the school's
 * `parent-online-payments` flag; while it is unknown (`canPay` undefined) they are not shown, and when it is off a
 * note says so.
 */
export function DuesList({ dues, canPay }: { dues: readonly Due[]; canPay?: boolean | undefined }) {
  // One Idempotency-Key per due and page visit: a retry after a lost answer reuses it and gets the same attempt.
  const keys = useRef(new Map<string, string>());
  const [busy, setBusy] = useState<string | null>(null);

  // Back from the checkout may restore this page from the back/forward cache with a button still busy.
  useEffect(() => {
    const restored = (event: PageTransitionEvent) => {
      if (event.persisted) setBusy(null);
    };
    window.addEventListener('pageshow', restored);
    return () => {
      window.removeEventListener('pageshow', restored);
    };
  }, []);
  const [error, setError] = useState<{ key: string; message: string } | null>(null);

  if (dues.length === 0) {
    return <p className="text-sm text-muted-foreground">{text.dues.empty}</p>;
  }

  async function pay(due: Due) {
    const key = dueKey(due);
    if (busy) return;
    let idempotencyKey = keys.current.get(key);
    if (!idempotencyKey) {
      idempotencyKey = crypto.randomUUID();
      keys.current.set(key, idempotencyKey);
    }
    setBusy(key);
    setError(null);
    try {
      const payment = await startOnlinePayment(due.enrollmentId, due.period, idempotencyKey);
      const target = checkoutTarget(payment.checkoutUrl);
      if (!target) {
        setError({ key, message: text.dues.badCheckout });
        setBusy(null);
        return;
      }
      browser.assign(target);
    } catch (failure) {
      setError({ key, message: errorMessage(failure) });
      setBusy(null);
    }
  }

  const total = owedTotal(dues);
  return (
    <div className="space-y-3">
      {canPay === false && (
        <p className="text-sm text-muted-foreground" data-testid="pay-unavailable">
          {text.dues.payUnavailable}
        </p>
      )}
      {total && (
        <p className="text-sm font-semibold" data-testid="dues-total">
          {format(text.dues.total, { amount: formatMoney(total) })}
        </p>
      )}
      <ul className="space-y-3">
        {dues.map((due) => {
          const key = dueKey(due);
          const period = formatPeriod(due.period);
          const discounted = due.listFee.amount !== due.fee.amount;
          return (
            <li key={key} className="rounded-lg border border-border p-3" data-testid="due-row">
              <div className="flex flex-wrap items-start justify-between gap-2">
                <div>
                  <p className="font-semibold">{period}</p>
                  <p className="text-xs text-muted-foreground">{due.groupName}</p>
                </div>
                <p className="text-right">
                  <span className="block text-xs text-muted-foreground">{text.dues.remaining}</span>
                  <span className="text-base font-semibold">{formatMoney(due.remaining.amount)}</span>
                </p>
              </div>
              <dl className="mt-2 grid grid-cols-2 gap-x-4 gap-y-1 text-xs">
                <dt className="text-muted-foreground">{text.dues.fee}</dt>
                <dd className="text-right">
                  {discounted && (
                    <s className="mr-2 text-muted-foreground" data-testid="list-fee">
                      <span className="sr-only">{text.dues.listFee}: </span>
                      {formatMoney(due.listFee.amount)}
                    </s>
                  )}
                  {formatMoney(due.fee.amount)}
                </dd>
                <dt className="text-muted-foreground">{text.dues.paid}</dt>
                <dd className="text-right">{formatMoney(due.paid.amount)}</dd>
              </dl>
              {due.adjustment === 'SiblingDiscount' && (
                <p className="badge badge-info mt-2" data-testid="discount-hint">
                  {format(text.dues.siblingDiscount, { percent: due.discountPercent })}
                </p>
              )}
              {due.adjustment === 'GroupChange' && (
                <p className="mt-2 text-xs text-muted-foreground">{text.dues.groupChange}</p>
              )}
              {error?.key === key && (
                <p role="alert" className="mt-2 text-sm text-destructive">
                  {error.message}
                </p>
              )}
              {canPay && (
                <button
                  type="button"
                  className="btn btn-primary mt-3 w-full sm:w-auto"
                  disabled={busy !== null}
                  aria-label={format(text.dues.payLabel, {
                    period,
                    amount: formatMoney(due.remaining.amount),
                  })}
                  onClick={() => void pay(due)}
                >
                  <CreditCard aria-hidden="true" />
                  {busy === key ? text.dues.paying : text.dues.pay}
                </button>
              )}
            </li>
          );
        })}
      </ul>
    </div>
  );
}
