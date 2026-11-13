import {
  LucideBuilding,
  LucideCalendarCheck,
  LucideCalendarDays,
  LucideChartColumn,
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
import { FeatureKey } from '../core/features/feature.service';
import { ui } from '../core/i18n/tr';

export interface NavItem {
  label: string;
  path: string;
  icon: LucideIconInput;
  /** P4-10: shown only while this feature flag is on for the user's school. */
  feature?: FeatureKey;
}

export interface NavSection {
  title?: string;
  items: NavItem[];
}

/**
 * Menu per role; routes are added as features land. Items behind a feature flag (P4-10) are left out while
 * `isEnabled` says the flag is off; a section left without items is dropped.
 */
export function navigationFor(
  role: Role | null,
  isEnabled: (feature: FeatureKey) => boolean = () => true,
): NavSection[] {
  return menuFor(role)
    .map((section) => ({
      ...section,
      items: section.items.filter((item) => !item.feature || isEnabled(item.feature)),
    }))
    .filter((section) => section.items.length > 0);
}

function menuFor(role: Role | null): NavSection[] {
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
        {
          items: [
            { label: ui.nav.dashboard, path: '/dashboard', icon: LucideLayoutDashboard },
            {
              label: ui.nav.reports,
              path: '/reports',
              icon: LucideChartColumn,
              feature: 'reports',
            },
          ],
        },
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
