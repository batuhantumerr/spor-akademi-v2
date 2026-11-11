/**
 * All user-facing Turkish text of the parent app lives here, so P3-10 can add an English dictionary
 * without touching components. Keep keys stable; components read `text.<area>.<key>`.
 */
export const text = {
  app: {
    name: 'Spor Akademi',
    tagline: 'Veli',
    skipToContent: 'İçeriğe geç',
    mainNavigation: 'Ana menü',
  },
  theme: {
    toggle: 'Temayı değiştir',
    light: 'Açık temaya geç',
    dark: 'Koyu temaya geç',
  },
  nav: {
    children: 'Çocuklarım',
  },
  children: {
    title: 'Çocuklarım',
    placeholder: 'Çocuklarınızın programı, yoklaması ve aidatları burada görünecek.',
  },
  notFound: {
    title: 'Sayfa bulunamadı',
    description: 'Aradığınız sayfa taşınmış veya hiç var olmamış olabilir.',
    backHome: 'Ana sayfaya dön',
  },
  errors: {
    unexpected: 'Beklenmeyen bir hata oluştu. Sayfayı yenileyip tekrar deneyin.',
  },
} as const;

/** Page title in the browser tab: "<page> · Spor Akademi". */
export function pageTitle(page: string): string {
  return `${page} · ${text.app.name}`;
}
