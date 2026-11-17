import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, map } from 'rxjs';

import { MfaEnrollmentStart } from './auth.service';

/** The signed-in user's own two-step verification state (P5-01). */
export interface MfaStatus {
  /** Whether the role may use two-step verification at all (School Admin and Platform Admin). */
  available: boolean;
  enabled: boolean;
  /** Mandatory for the role: it cannot be turned off. */
  required: boolean;
  recoveryCodesRemaining: number;
}

/** Why a Platform Admin resets someone's two-step verification; a fixed list that goes to the audit log. */
export type MfaResetReason = 'LostDevice' | 'SuspectedCompromise' | 'Other';

/** Two-step verification calls of a signed-in user (the sign-in steps are in AuthService). */
@Injectable({ providedIn: 'root' })
export class MfaService {
  private readonly http = inject(HttpClient);

  status(): Observable<MfaStatus> {
    return this.http.get<MfaStatus>('/api/v1/me/mfa');
  }

  startEnrollment(): Observable<MfaEnrollmentStart> {
    return this.http.post<MfaEnrollmentStart>('/api/v1/me/mfa/enrollment', null);
  }

  /** Confirms the enrolment with a code and returns the recovery codes, shown once. */
  confirmEnrollment(code: string): Observable<string[]> {
    return this.http
      .post<{ codes: string[] }>('/api/v1/me/mfa/enrollment/confirm', { code })
      .pipe(map((response) => response.codes));
  }

  disable(code: string): Observable<void> {
    return this.http.post<void>('/api/v1/me/mfa/disable', { code });
  }

  /** Platform Admin only: removes another user's two-step verification (a lost phone). */
  reset(userId: string, reason: MfaResetReason): Observable<void> {
    return this.http.post<void>(`/api/v1/platform/users/${userId}/mfa/reset`, { reason });
  }
}
