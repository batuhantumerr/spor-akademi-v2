import { Languages } from 'lucide-react';
import { LANGUAGES, LANGUAGE_NAMES, setLanguage, useLanguage } from '../i18n/language';
import { text } from '../i18n/tr';

/**
 * Switches between Turkish and English (P3-10, DEC-428). The button names the other language in that language
 * ("English" / "Türkçe", with its `lang`), so a parent who cannot read the current one still finds it.
 */
export function LanguageSwitcher({ className = '' }: { className?: string }) {
  const current = useLanguage();
  const target = LANGUAGES.find((language) => language !== current) ?? 'tr';
  return (
    <button
      type="button"
      className={`btn btn-ghost btn-sm gap-1.5 ${className}`}
      title={text.language.change}
      data-testid="language-switcher"
      onClick={() => {
        setLanguage(target);
      }}
    >
      <Languages aria-hidden="true" className="size-4" />
      <span lang={target}>{LANGUAGE_NAMES[target]}</span>
    </button>
  );
}
