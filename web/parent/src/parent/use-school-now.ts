import { useEffect, useState } from 'react';
import { useSession } from '../auth/session';
import { schoolNow, type SchoolNow } from '../format/school-time';

const TICK_MS = 60_000;

/** "Now" in the school's time zone, re-read every minute so an open page moves on to the next training. */
export function useSchoolNow(): SchoolNow {
  const timeZone = useSession().user?.schoolTimeZone ?? null;
  const [now, setNow] = useState(() => new Date());
  useEffect(() => {
    const timer = window.setInterval(() => {
      setNow(new Date());
    }, TICK_MS);
    return () => {
      window.clearInterval(timer);
    };
  }, []);
  return schoolNow(now, timeZone);
}
