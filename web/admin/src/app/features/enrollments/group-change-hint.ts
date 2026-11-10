import { Signal, computed, signal } from '@angular/core';

import { Api } from '../../api/api';
import { apiV1SettingsBillingRulesGet } from '../../api/functions';
import { BillingRulesDto } from '../../api/models';
import { ui } from '../../core/i18n/tr';

/**
 * P2-F8 (DEC-372): the fee hint next to an enrollment's start or end date. Overlapping enrollments are always charged
 * twice for the month; "one fee for the month" is promised only when the fee rules version in force for the date's
 * month has the single charge on. Without the rules (not loaded, failed or none) only the neutral overlap warning shows.
 */
export class GroupChangeHint {
  private readonly versions = signal<BillingRulesDto[] | null>(null);

  constructor(
    api: Api,
    private readonly date: Signal<string>,
  ) {
    api.invoke(apiV1SettingsBillingRulesGet).then(
      (versions) => this.versions.set(versions),
      () => this.versions.set(null),
    );
  }

  /** Whether the version in force for the date's month charges a group change once. */
  readonly singleCharge = computed(() => {
    const month = this.date().slice(0, 7);
    const inForce = (this.versions() ?? [])
      .filter((v) => v.effectiveFrom <= month)
      .sort((a, b) => b.effectiveFrom.localeCompare(a.effectiveFrom))[0];
    return inForce?.singleChargeOnGroupChange === true;
  });

  readonly text = computed(() =>
    this.singleCharge()
      ? `${ui.enrollments.singleChargeHint} ${ui.enrollments.overlapHint}`
      : ui.enrollments.overlapHint,
  );
}
