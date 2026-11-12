import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { LucideLanguages } from '@lucide/angular';

import { LANGUAGE_NAMES, LanguageService } from '../../core/i18n/language.service';
import { ui } from '../../core/i18n/tr';

/**
 * Switches between Turkish and English (P3-10, DEC-428). The button names the other language in that language
 * ("English" / "Türkçe", marked with its `lang`), so a user who cannot read the current one still finds it.
 */
@Component({
  selector: 'app-language-switcher',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [LucideLanguages],
  template: `
    <button
      type="button"
      class="btn btn-ghost h-9 gap-1.5 px-2.5 text-sm"
      data-testid="language-switcher"
      [attr.title]="text.change"
      (click)="switch()"
    >
      <svg lucideLanguages class="size-4" aria-hidden="true"></svg>
      <span [attr.lang]="target()">{{ targetName() }}</span>
    </button>
  `,
})
export class LanguageSwitcherComponent {
  private readonly languages = inject(LanguageService);
  protected readonly text = ui.language;
  protected readonly target = computed(() => {
    this.languages.current();
    return this.languages.other();
  });
  protected readonly targetName = computed(() => LANGUAGE_NAMES[this.target()]);

  protected switch(): void {
    this.languages.set(this.target());
  }
}
