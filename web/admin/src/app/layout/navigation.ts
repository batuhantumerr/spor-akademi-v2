import {
  LucideBuilding,
  LucideCalendarCheck,
  LucideCalendarDays,
  LucideCreditCard,
  LucideDumbbell,
  LucideGraduationCap,
  LucideIconInput,
  LucideLayoutDashboard,
  LucideListChecks,
  LucideMapPin,
  LucideMegaphone,
  LucidePercent,
  LucideReceipt,
  LucideShieldCheck,
  LucideUsers,
  LucideWallet,
  LucideWhistle,
} from '@lucide/angular';

import { Role } from '../core/auth/auth.service';
import { ui } from '../core/i18n/tr';

export interface NavItem {
  label: string;
  path: string;
  icon: LucideIconInput;
}

export interface NavSection {
  title?: string;
  items: NavItem[];
}

/** Menu per role; routes are added as features land. */
export function navigationFor(role: Role | null): NavSection[] {
  switch (role) {
    case 'PlatformAdmin':
      return [
        {
          items: [
            { label: ui.nav.schools, path: '/platform/schools', icon: LucideBuilding },
            { label: ui.nav.jobs, path: '/platform/jobs', icon: LucideListChecks },
          ],
        },
      ];
    case 'SchoolAdmin':
      return [
        { items: [{ label: ui.nav.dashboard, path: '/dashboard', icon: LucideLayoutDashboard }] },
        {
          title: ui.nav.setup,
          items: [
            { label: ui.nav.sports, path: '/sports', icon: LucideDumbbell },
            { label: ui.nav.facilities, path: '/facilities', icon: LucideMapPin },
            { label: ui.nav.coaches, path: '/coaches', icon: LucideWhistle },
          ],
        },
        {
          title: ui.nav.overview,
          items: [
            { label: ui.nav.groups, path: '/groups', icon: LucideCalendarDays },
            { label: ui.nav.students, path: '/students', icon: LucideGraduationCap },
            { label: ui.nav.attendance, path: '/attendance', icon: LucideCalendarCheck },
          ],
        },
        {
          title: ui.nav.finance,
          items: [
            { label: ui.nav.payments, path: '/payments', icon: LucideWallet },
            { label: ui.nav.dues, path: '/dues', icon: LucideReceipt },
            { label: ui.nav.onlinePayments, path: '/online-payments', icon: LucideCreditCard },
          ],
        },
        {
          title: ui.nav.settings,
          items: [
            {
              label: ui.nav.dataRetention,
              path: '/settings/data-retention',
              icon: LucideShieldCheck,
            },
            { label: ui.nav.billingRules, path: '/settings/billing-rules', icon: LucidePercent },
          ],
        },
      ];
    case 'Coach':
      return [
        {
          items: [
            { label: ui.nav.today, path: '/coach/today', icon: LucideCalendarCheck },
            { label: ui.nav.myGroups, path: '/coach/groups', icon: LucideUsers },
            { label: ui.nav.announcements, path: '/coach/announcements', icon: LucideMegaphone },
          ],
        },
      ];
    default:
      return [];
  }
}
