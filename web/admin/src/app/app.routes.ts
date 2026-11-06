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
        children: [{ path: 'schools', title: ui.nav.schools, ...placeholder(ui.nav.schools) }],
      },
      {
        path: 'coach',
        canActivate: [roleGuard('Coach')],
        children: [
          { path: 'today', title: ui.nav.today, ...placeholder(ui.nav.today) },
          { path: 'groups', title: ui.nav.myGroups, ...placeholder(ui.nav.myGroups) },
        ],
      },
      {
        path: '',
        canActivate: [roleGuard('SchoolAdmin')],
        children: [
          { path: 'dashboard', title: ui.nav.dashboard, ...placeholder(ui.nav.dashboard) },
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
          { path: 'coaches', title: ui.nav.coaches, ...placeholder(ui.nav.coaches) },
          { path: 'groups', title: ui.nav.groups, ...placeholder(ui.nav.groups) },
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
          { path: 'payments', title: ui.nav.payments, ...placeholder(ui.nav.payments) },
          { path: 'dues', title: ui.nav.dues, ...placeholder(ui.nav.dues) },
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
