import { CalendarClock, ChevronRight, CircleCheck, Wallet } from 'lucide-react';
import type { ReactNode } from 'react';
import { Link } from 'react-router';
import { formatMoney } from '../format/format';
import { format, text } from '../i18n/tr';
import { useAttendance, useChildren, useDues, useSchedule, type Child } from '../parent/queries';
import { childName, nextTraining, owedTotal, todayAttendance } from '../parent/summary';
import { useSchoolNow } from '../parent/use-school-now';
import { Empty, ErrorState, Loading } from '../ui/states';
import { useDocumentTitle } from './use-document-title';

/** P3-06 home: one card per child with the next training, today's attendance and what is owed. */
export function ChildrenPage() {
  useDocumentTitle(text.children.title);
  const children = useChildren();

  return (
    <section aria-labelledby="children-title" className="space-y-4">
      <h1 id="children-title">{text.children.title}</h1>
      {children.isPending ? (
        <Loading />
      ) : children.isError ? (
        <ErrorState error={children.error} onRetry={() => void children.refetch()} />
      ) : children.data.length === 0 ? (
        <div className="card p-5">
          <Empty>{text.children.empty}</Empty>
        </div>
      ) : (
        <ul className="space-y-4">
          {children.data.map((child) => (
            <li key={child.id}>
              <ChildCard child={child} />
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}

function ChildCard({ child }: { child: Child }) {
  const name = childName(child);
  return (
    <article aria-labelledby={`child-${child.id}`} className="card overflow-hidden">
      <div className="flex items-center justify-between gap-3 border-b border-border px-4 py-3">
        <h2 id={`child-${child.id}`} className="text-lg">
          {name}
        </h2>
        <Link
          to={`/cocuk/${child.id}`}
          className="btn btn-ghost btn-sm -mr-2"
          aria-label={format(text.children.detailsLabel, { name })}
        >
          {text.children.details}
          <ChevronRight aria-hidden="true" />
        </Link>
      </div>
      <ChildSummary childId={child.id} />
    </article>
  );
}

/** The three facts, each from its own query so one failing does not hide the others. */
export function ChildSummary({ childId }: { childId: string }) {
  const now = useSchoolNow();
  const schedule = useSchedule(childId);
  const attendance = useAttendance(childId);
  const dues = useDues(childId);

  const training = schedule.data ? nextTraining(schedule.data, now) : null;
  const today = attendance.data ? todayAttendance(attendance.data, now.date) : null;
  const owed = dues.data ? owedTotal(dues.data) : null;

  return (
    <dl className="divide-y divide-border">
      <Fact icon={<CalendarClock />} label={text.children.nextTraining} query={schedule}>
        {training ? (
          <>
            <span className="font-semibold">{training.when}</span>
            <span className="block text-xs text-muted-foreground">{training.where}</span>
          </>
        ) : (
          <span className="text-muted-foreground">{text.children.noTraining}</span>
        )}
      </Fact>
      {(today !== null || attendance.isPending || attendance.isError) && (
        <Fact icon={<CircleCheck />} label={text.children.today} query={attendance}>
          {today && (
            <span className={today === 'Absent' ? 'font-semibold text-destructive' : 'font-semibold'}>
              {text.todayAttendance[today]}
            </span>
          )}
        </Fact>
      )}
      <Fact icon={<Wallet />} label={text.children.owed} query={dues}>
        {owed ? (
          <span className="font-semibold text-warning" data-testid="owed-total">
            {formatMoney(owed)}
          </span>
        ) : (
          <span className="text-muted-foreground">{text.children.nothingOwed}</span>
        )}
      </Fact>
    </dl>
  );
}

function Fact({
  icon,
  label,
  query,
  children,
}: {
  icon: ReactNode;
  label: string;
  query: { isPending: boolean; isError: boolean };
  children: ReactNode;
}) {
  return (
    <div className="px-4 py-3 text-sm">
      <dt className="flex items-center gap-2 text-xs font-medium text-muted-foreground">
        <span aria-hidden="true" className="[&_svg]:size-4">
          {icon}
        </span>
        {label}
      </dt>
      <dd className="mt-0.5 pl-6">
        {query.isPending ? (
          <span className="text-muted-foreground">{text.children.loadingSummary}</span>
        ) : query.isError ? (
          <span className="text-destructive">{text.children.summaryFailed}</span>
        ) : (
          children
        )}
      </dd>
    </div>
  );
}
