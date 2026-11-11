import { ArrowLeft } from 'lucide-react';
import { Link, useParams } from 'react-router';
import { toApiError } from '../api/errors';
import { useSession } from '../auth/session';
import {
  formatDate,
  formatDateTime,
  formatPercent,
  formatPeriod,
  formatTime,
  formatMoney,
} from '../format/format';
import { format, text } from '../i18n/tr';
import { DuesList } from '../parent/DuesList';
import {
  useAnnouncements,
  useAttendance,
  useChildren,
  useDues,
  usePayments,
  useSchedule,
  type ChildAttendance,
  type ChildGroup,
} from '../parent/queries';
import { Empty, ErrorState, Loading, Section } from '../ui/states';
import { childName } from '../parent/summary';
import { ChildSummary } from './ChildrenPage';
import { NotFoundPage } from './NotFoundPage';
import { useDocumentTitle } from './use-document-title';

const SECTIONS = [
  { id: 'aidatlar', label: text.dues.title },
  { id: 'program', label: text.schedule.title },
  { id: 'yoklama', label: text.attendance.title },
  { id: 'odemeler', label: text.payments.title },
  { id: 'duyurular', label: text.announcements.title },
] as const;

/**
 * P3-06 child detail, one scrolling page on a phone: the ten-second summary first, then dues (what is owed and the
 * pay button), the weekly program, attendance, payments and announcements. A jump bar links the sections.
 */
export function ChildPage() {
  const { id = '' } = useParams();
  const children = useChildren();
  const child = children.data?.find((c) => c.id === id);
  useDocumentTitle(child ? childName(child) : text.children.title);

  if (children.isPending) return <Loading />;
  if (children.isError) return <ErrorState error={children.error} onRetry={() => void children.refetch()} />;
  if (!child) return <NotFoundPage message={text.child.notFound} />;

  const name = childName(child);
  return (
    <div className="space-y-4">
      <Link to="/" className="btn btn-ghost btn-sm -ml-3">
        <ArrowLeft aria-hidden="true" />
        {text.child.backToList}
      </Link>
      <header className="space-y-1">
        <h1>{name}</h1>
        {child.birthDate && (
          <p className="text-sm text-muted-foreground">
            {format(text.child.born, { date: formatDate(child.birthDate) })}
          </p>
        )}
      </header>

      <div className="card overflow-hidden">
        <ChildSummary childId={id} />
      </div>

      <nav
        aria-label={text.child.sections}
        className="sticky top-14 z-30 -mx-4 overflow-x-auto border-y border-border bg-background/95 px-4 py-2 backdrop-blur"
      >
        <ul className="flex gap-2">
          {SECTIONS.map((section) => (
            <li key={section.id}>
              <a href={`#${section.id}`} className="btn btn-secondary btn-sm">
                {section.label}
              </a>
            </li>
          ))}
        </ul>
      </nav>

      <DuesSection childId={id} />
      <ScheduleSection childId={id} />
      <AttendanceSection childId={id} />
      <PaymentsSection childId={id} />
      <AnnouncementsSection childId={id} />
    </div>
  );
}

/** A query whose 404 means the child is no longer linked; the whole page then reads as not found. */
function failure(error: unknown, retry: () => void) {
  return <ErrorState error={error} onRetry={toApiError(error).status === 404 ? undefined : retry} />;
}

function DuesSection({ childId }: { childId: string }) {
  const dues = useDues(childId);
  return (
    <Section id="aidatlar" title={text.dues.title}>
      {dues.isPending ? (
        <Loading />
      ) : dues.isError ? (
        failure(dues.error, () => void dues.refetch())
      ) : (
        <DuesList dues={dues.data} />
      )}
    </Section>
  );
}

function ScheduleSection({ childId }: { childId: string }) {
  const schedule = useSchedule(childId);
  return (
    <Section id="program" title={text.schedule.title}>
      {schedule.isPending ? (
        <Loading />
      ) : schedule.isError ? (
        failure(schedule.error, () => void schedule.refetch())
      ) : schedule.data.length === 0 ? (
        <Empty>{text.schedule.empty}</Empty>
      ) : (
        <ul className="space-y-4">
          {schedule.data.map((group) => (
            <li key={group.enrollmentId}>
              <GroupProgram group={group} />
            </li>
          ))}
        </ul>
      )}
    </Section>
  );
}

const DAY_ORDER = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday'];

function GroupProgram({ group }: { group: ChildGroup }) {
  const coach = [group.coachFirstName, group.coachLastName].filter(Boolean).join(' ');
  const slots = [...group.slots].sort(
    (a, b) => DAY_ORDER.indexOf(a.day) - DAY_ORDER.indexOf(b.day) || a.start.localeCompare(b.start),
  );
  return (
    <div>
      <h3 className="text-base font-semibold">{group.groupName}</h3>
      <p className="text-xs text-muted-foreground">
        {group.sportName} · {group.facilityName}
        {coach && ` · ${format(text.schedule.coach, { name: coach })}`}
      </p>
      <ul className="mt-2 divide-y divide-border rounded-lg border border-border text-sm">
        {slots.map((slot) => (
          <li key={`${slot.day}-${slot.start}`} className="flex justify-between px-3 py-2">
            <span>{text.days[slot.day] ?? slot.day}</span>
            <span className="font-mono">
              {formatTime(slot.start)}–{formatTime(slot.end)}
            </span>
          </li>
        ))}
      </ul>
      <p className="mt-1 text-xs text-muted-foreground">
        {format(text.schedule.since, { date: formatDate(group.startDate) })}
      </p>
    </div>
  );
}

function AttendanceSection({ childId }: { childId: string }) {
  const attendance = useAttendance(childId);
  return (
    <Section id="yoklama" title={text.attendance.title}>
      {attendance.isPending ? (
        <Loading />
      ) : attendance.isError ? (
        failure(attendance.error, () => void attendance.refetch())
      ) : (
        <AttendanceList attendance={attendance.data} />
      )}
    </Section>
  );
}

function AttendanceList({ attendance }: { attendance: ChildAttendance }) {
  const { summary } = attendance;
  const sessions = [...attendance.sessions].sort((a, b) =>
    b.date === a.date ? b.start.localeCompare(a.start) : b.date.localeCompare(a.date),
  );
  const rate = summary.rate === null ? null : Number(summary.rate);
  return (
    <div className="space-y-3">
      <p className="text-xs text-muted-foreground">
        {text.attendance.range} ({formatDate(attendance.from)} – {formatDate(attendance.to)})
      </p>
      {rate !== null && (
        <p className="text-sm font-semibold">{format(text.attendance.rate, { rate: formatPercent(rate) })}</p>
      )}
      <p className="text-xs text-muted-foreground">
        {format(text.attendance.counts, {
          present: summary.present,
          late: summary.late,
          absent: summary.absent,
          excused: summary.excused,
        })}
      </p>
      {sessions.length === 0 ? (
        <Empty>{text.attendance.empty}</Empty>
      ) : (
        <ul className="divide-y divide-border rounded-lg border border-border text-sm">
          {sessions.map((session) => {
            const status = session.status ?? 'NotRecorded';
            return (
              <li
                key={`${session.groupId}-${session.sessionKey}`}
                className="flex items-center justify-between gap-3 px-3 py-2"
              >
                <span>
                  {formatDate(session.date)}{' '}
                  <span className="font-mono text-xs">{formatTime(session.start)}</span>
                  <span className="block text-xs text-muted-foreground">{session.groupName}</span>
                </span>
                <span className={`badge ${statusTone(status)}`}>{text.attendance.statuses[status]}</span>
              </li>
            );
          })}
        </ul>
      )}
    </div>
  );
}

function statusTone(status: string): string {
  switch (status) {
    case 'Present':
      return 'badge-success';
    case 'Late':
      return 'badge-warning';
    case 'Absent':
      return 'badge-danger';
    case 'Excused':
      return 'badge-info';
    default:
      return 'badge-muted';
  }
}

function PaymentsSection({ childId }: { childId: string }) {
  const payments = usePayments(childId);
  const items = payments.data?.pages.flatMap((page) => page.items) ?? [];
  return (
    <Section id="odemeler" title={text.payments.title}>
      {payments.isPending ? (
        <Loading />
      ) : payments.isError ? (
        failure(payments.error, () => void payments.refetch())
      ) : items.length === 0 ? (
        <Empty>{text.payments.empty}</Empty>
      ) : (
        <>
          <ul
            className="divide-y divide-border rounded-lg border border-border text-sm"
            data-testid="payments"
          >
            {items.map((payment) => (
              <li key={payment.id} className="flex items-center justify-between gap-3 px-3 py-2">
                <span>
                  {formatPeriod(payment.period)}
                  <span className="block text-xs text-muted-foreground">
                    {formatDate(payment.paidOn)} · {text.payments.methods[payment.method] ?? payment.method}
                  </span>
                </span>
                <span className="font-semibold">{formatMoney(payment.amount.amount)}</span>
              </li>
            ))}
          </ul>
          {payments.hasNextPage && (
            <button
              type="button"
              className="btn btn-secondary btn-sm mt-3"
              disabled={payments.isFetchingNextPage}
              onClick={() => void payments.fetchNextPage()}
            >
              {text.payments.more}
            </button>
          )}
        </>
      )}
    </Section>
  );
}

function AnnouncementsSection({ childId }: { childId: string }) {
  const timeZone = useSession().user?.schoolTimeZone ?? null;
  const announcements = useAnnouncements(childId);
  const items = announcements.data?.pages.flatMap((page) => page.items) ?? [];
  return (
    <Section id="duyurular" title={text.announcements.title}>
      {announcements.isPending ? (
        <Loading />
      ) : announcements.isError ? (
        failure(announcements.error, () => void announcements.refetch())
      ) : items.length === 0 ? (
        <Empty>{text.announcements.empty}</Empty>
      ) : (
        <>
          <ul className="space-y-3">
            {items.map((announcement) => (
              <li key={announcement.id} className="rounded-lg border border-border p-3">
                <h3 className="text-base font-semibold">{announcement.title}</h3>
                <p className="text-xs text-muted-foreground">
                  {announcement.groupName} · {formatDateTime(announcement.publishedAt, timeZone)}
                </p>
                <p className="mt-2 text-sm whitespace-pre-line">{announcement.body}</p>
              </li>
            ))}
          </ul>
          {announcements.hasNextPage && (
            <button
              type="button"
              className="btn btn-secondary btn-sm mt-3"
              disabled={announcements.isFetchingNextPage}
              onClick={() => void announcements.fetchNextPage()}
            >
              {text.announcements.more}
            </button>
          )}
        </>
      )}
    </Section>
  );
}
