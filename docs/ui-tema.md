# Admin arayüzü tema rehberi

Sahibin isteği (2026-11-06): arayüz modern, anlaşılır, grafiklerle desteklenmiş olsun; görsel dil sahibin başka bir projesindeki (React + Tailwind + Radix/shadcn) temaya benzesin. O projenin **marka adı, logosu ve yeşil marka renkleri kullanılmaz**; sadece yapı, nötr renkler ve bileşen kalıpları alınır. Uygulama Angular'da kalır (DEC-024). Seçim ADR-0010'da kayıtlıdır.

## Altyapı

- Tailwind CSS v4 (`@tailwindcss/postcss`), config dosyası yok; token'lar `src/styles/theme.css` içinde `:root` / `.dark` + `@theme inline` ile.
- Karanlık mod: `<html>` üzerinde `dark` / `light` sınıfı; tercih `localStorage` (`sa-theme`: `light|dark|system`), yoksa `prefers-color-scheme`. İlk boyamadan önce küçük bir inline script uygular.
- Font: Inter (gövde), JetBrains Mono (sayılar, tablo başlıkları). Kök font boyutu 14px.
- İkonlar: lucide (`@lucide/angular` veya eşdeğeri).
- Grafikler: Apache ECharts (`ngx-echarts`, ADR-0010; ApexCharts ticari lisans nedeniyle reddedildi). Renkler CSS değişkenlerinden `getComputedStyle` ile okunur, tema değişince yeniden uygulanır.

## Token'lar (hex)

| token | light | dark |
|---|---|---|
| background / foreground | #F8FAFC / #0F172A | #121620 / #EDF2F7 |
| card / card-foreground | #FFFFFF / #0F172A | #1A202C / #EDF2F7 |
| popover / popover-foreground | #FFFFFF / #0F172A | #1A202C / #EDF2F7 |
| primary / primary-foreground | #1D4ED8 / #FFFFFF | #60A5FA / #121620 |
| secondary / secondary-foreground | #F1F5F9 / #475569 | #1E2736 / #A0AEC0 |
| muted / muted-foreground | #F1F5F9 / #64748B | #1E2736 / #A0AEC0 |
| accent / accent-foreground | #1D4ED8 / #FFFFFF | #60A5FA / #121620 |
| destructive / destructive-foreground | #E63946 / #FFFFFF | #E63946 / #EDF2F7 |
| border | #E2E8F0 | #2D3748 |
| input | #FFFFFF | #1A202C |
| ring | #2563EB | #60A5FA |
| chart-1 | #2563EB | #60A5FA |
| chart-2 | #D97706 | #FFB703 |
| chart-3 | #16A34A | #4ADE80 |
| chart-4 | #7C3AED | #A78BFA |
| chart-5 | #DC2626 | #E63946 |
| sidebar / sidebar-foreground | #FFFFFF / #475569 | #0D1117 / #A0AEC0 |
| sidebar-primary / -foreground | #1D4ED8 / #FFFFFF | #60A5FA / #121620 |
| sidebar-accent / -foreground | #F1F5F9 / #0F172A | #1A202C / #EDF2F7 |
| success / warning / info | #16A34A / #D97706 / #2563EB | #4ADE80 / #F39C12 / #3B82F6 |

Radius: `--radius: 0.25rem`; `sm = r-2px`, `md = r`, `lg = r+2px`, `xl = r+4px`. Stat kartları `rounded-3xl`.

## Kabuk (layout)

- Kök: `h-screen flex overflow-hidden bg-background`.
- Sidebar: `fixed h-full border-r`, açık `w-64`, kapalı `w-20`; `lg` altında `bg-black/50` arka planlı çekmece. Üstte `h-16 px-4 border-b` logo alanı (`h-8 w-8 rounded-lg bg-primary/10 border border-primary/20`). Bölüm başlıkları `text-[10px] font-bold uppercase tracking-widest text-muted-foreground`.
- Menü öğesi: `flex h-9 items-center gap-2.5 px-3 rounded-lg text-sm font-medium`; aktif `bg-gradient-to-r from-primary/20 to-primary/5 text-primary border border-primary/30 shadow-lg shadow-primary/5`; pasif `text-muted-foreground hover:text-foreground hover:bg-muted/50`.
- Header: `sticky top-0 h-16 border-b border-border/50 bg-card/80 backdrop-blur-xl px-2 sm:px-6`; sağda tema düğmesi ve kullanıcı menüsü.
- İçerik: `flex-1 overflow-y-auto p-4 md:p-6`.
- Sayfa başlığı: `h1.text-xl.font-bold.tracking-tight` + `p.text-sm.text-muted-foreground`; aksiyonlar `flex flex-col md:flex-row md:items-center justify-between gap-4`.

## Bileşen kalıpları

- **Card:** `bg-card text-card-foreground rounded-xl border`, başlık `px-4 pt-4`, içerik `p-4`.
- **Table:** thead `bg-muted/60 border-b`; th `h-10 px-4 text-[10px] md:text-xs font-semibold uppercase tracking-wider font-mono text-muted-foreground`; satır `border-b border-border/60 hover:bg-muted/40`; td `px-4 py-3 text-sm`.
- **Button:** taban `inline-flex items-center gap-2 rounded-md text-sm font-semibold transition-all active:scale-95 focus-visible:ring-2 ring-primary/50 disabled:opacity-40`; default `bg-primary text-primary-foreground shadow-lg shadow-primary/10 hover:bg-primary/90`; outline `border-2 border-border text-muted-foreground hover:border-primary hover:text-primary hover:bg-primary/5`; secondary `bg-card border hover:bg-muted`; ghost `text-muted-foreground hover:text-primary hover:bg-primary/10`; destructive `bg-red-600 text-white hover:bg-red-700`. Boyutlar `h-10 px-5`, sm `h-9 px-3 text-xs`, icon `size-10`.
- **Badge:** `rounded-full border px-2.5 py-0.5 text-[10px] font-bold uppercase tracking-wider`; renkli varyant `border-X/30 bg-X/10 text-X`.
- **Input:** `h-9 rounded-md border bg-card/50 px-3 placeholder:text-muted-foreground/50 focus-visible:border-primary focus-visible:ring-[3px] focus-visible:ring-primary/20 aria-invalid:border-destructive`.
- **Dialog:** overlay `bg-slate-900/60`; içerik `bg-background rounded-lg border p-6 shadow-lg sm:max-w-lg`.
- **Tabs:** liste `bg-muted rounded-xl p-[3px]`; tetikleyici `rounded-xl px-3 py-2 text-sm font-medium`, aktif `bg-card`.
- **KPI kartı:** `p-4 rounded-3xl border bg-card shadow-sm hover:shadow-md relative overflow-hidden`, arkada `w-44 h-44 rounded-full blur-3xl opacity-[0.06]` renk parlaması; başlık `text-[11px] font-bold uppercase tracking-wider text-muted-foreground`; değer `text-2xl font-extrabold tracking-tight font-mono`.

## Grafik varsayılanları (ECharts, `app-chart`)

Şeffaf arka plan, Inter font, renkler `--chart-1..5` token'larından, eksen çizgi ve tikleri gizli, yatay ızgara çizgileri `--border` renginde kesikli, tooltip `--popover` renginde. Kullanılacak grafikler: devam oranı trendi (area), gruplara göre doluluk (bar), aylık tahsilat/borç (bar), yoklama dağılımı (donut), grup aylık yoklama tablosu (heatmap).
