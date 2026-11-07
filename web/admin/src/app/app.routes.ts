import { Routes } from '@angular/router';

import {
  authGuard,
  guestGuard,
  homeRedirectGuard,
  roleGuard,
  sessionGuard,
} from './core/auth/auth.guards';
import { ui } from './core/i18n/tr';

const placeholder = (title: string) => ({
  loadComponent: () =>
    import('./features/placeholder/placeholder.page').then((m) => m.PlaceholderPage),
  data: { title },
});

export const routes: Routes = [
  {
    path: 'login',
    canActivate: [guestGuard],
    loadComponent: () => import('./features/auth/login.page').then((m) => m.LoginPage),
    title: ui.login.title,
  },
  {
    path: 'change-password',
    canActivate: [sessionGuard],
    loadComponent: () =>
      import('./features/auth/change-password.page').then((m) => m.ChangePasswordPage),
    title: ui.changePassword.title,
  },
  {
    path: '',
    canActivate: [authGuard],
    loadComponent: () => import('./layout/shell.component').then((m) => m.ShellComponent),
    children: [
      { path: '', pathMatch: 'full', canActivate: [homeRedirectGuard], children: [] },
      {
        path: 'platform',
        canActivate: [roleGuard('PlatformAdmin')],
        children: [
          {
            path: 'schools',
            title: ui.nav.schools,
            loadComponent: () =>
              import('./features/platform/schools.page').then((m) => m.SchoolsPage),
          },
          {
            path: 'jobs',
            title: ui.nav.jobs,
            loadComponent: () => import('./features/platform/jobs.page').then((m) => m.JobsPage),
          },
        ],
      },
      {
        path: 'coach',
        canActivate: [roleGuard('Coach')],
        children: [
          {
            path: 'today',
            title: ui.nav.today,
            loadComponent: () =>
              import('./features/coach/coach-today.page').then((m) => m.CoachTodayPage),
          },
          {
            path: 'groups',
            title: ui.nav.myGroups,
            loadComponent: () =>
              import('./features/coach/coach-groups.page').then((m) => m.CoachGroupsPage),
          },
          {
            path: 'sessions/:groupId/:sessionKey',
            title: ui.attendance.title,
            loadComponent: () =>
              import('./features/attendance/take-attendance.page').then(
                (m) => m.TakeAttendancePage,
              ),
          },
          {
            path: 'announcements',
            title: ui.announcements.title,
            loadComponent: () =>
              import('./features/announcements/coach-announcements.page').then(
                (m) => m.CoachAnnouncementsPage,
              ),
          },
          {
            path: 'groups/:id',
            title: ui.roster.title,
            loadComponent: () =>
              import('./features/coach/coach-roster.page').then((m) => m.CoachRosterPage),
          },
        ],
      },
      {
        path: '',
        canActivate: [roleGuard('SchoolAdmin')],
        children: [
          {
            path: 'dashboard',
            title: ui.nav.dashboard,
            loadComponent: () =>
              import('./features/dashboard/dashboard.page').then((m) => m.DashboardPage),
          },
          {
            path: 'sports',
            title: ui.nav.sports,
            loadComponent: () =>
              import('./features/organization/sports.page').then((m) => m.SportsPage),
          },
          {
            path: 'facilities',
            title: ui.nav.facilities,
            loadComponent: () =>
              import('./features/organization/facilities.page').then((m) => m.FacilitiesPage),
          },
          {
            path: 'coaches',
            title: ui.nav.coaches,
            loadComponent: () =>
              import('./features/coaches/coaches.page').then((m) => m.CoachesPage),
          },
          {
            path: 'groups',
            title: ui.nav.groups,
            loadComponent: () => import('./features/groups/groups.page').then((m) => m.GroupsPage),
          },
          {
            path: 'groups/:groupId/sessions/:sessionKey',
            title: ui.attendance.title,
            loadComponent: () =>
              import('./features/attendance/take-attendance.page').then(
                (m) => m.TakeAttendancePage,
              ),
          },
          {
            path: 'groups/:id',
            title: ui.nav.groups,
            loadComponent: () =>
              import('./features/groups/group-detail.page').then((m) => m.GroupDetailPage),
          },
          {
            path: 'students',
            title: ui.nav.students,
            loadComponent: () =>
              import('./features/students/students.page').then((m) => m.StudentsPage),
          },
          {
            path: 'students/new',
            title: ui.studentForm.title,
            loadComponent: () =>
              import('./features/students/register-student.page').then(
                (m) => m.RegisterStudentPage,
              ),
          },
          {
            path: 'students/:id',
            title: ui.nav.students,
            loadComponent: () =>
              import('./features/students/student-detail.page').then((m) => m.StudentDetailPage),
          },
          { path: 'attendance', title: ui.nav.attendance, ...placeholder(ui.nav.attendance) },
          {
            path: 'payments',
            title: ui.nav.payments,
            loadComponent: () =>
              import('./features/billing/payments.page').then((m) => m.PaymentsPage),
          },
          {
            path: 'dues',
            title: ui.nav.dues,
            loadComponent: () => import('./features/billing/dues.page').then((m) => m.DuesPage),
          },
        ],
      },
    ],
  },
  {
    path: '**',
    loadComponent: () =>
      import('./features/placeholder/not-found.page').then((m) => m.NotFoundPage),
    title: ui.common.notFoundTitle,
  },
];
