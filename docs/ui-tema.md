# Admin arayüzü tema rehberi

Sahibin isteği (2026-11-06): arayüz modern, anlaşılır, grafiklerle desteklenmiş olsun; görsel dil sahibin başka bir projesindeki (React + Tailwind + Radix/shadcn) temaya benzesin. O projenin **marka adı, logosu ve yeşil marka renkleri kullanılmaz**; sadece yapı, nötr renkler ve bileşen kalıpları alınır. Uygulama Angular'da kalır (DEC-024). Seçim ADR-0010'da kayıtlıdır.

## Altyapı

- Tailwind CSS v4 (`@tailwindcss/postcss`), config dosyası yok; token'lar `web/admin/src/styles.css` içinde `:root` / `.dark` + `@theme inline` ile.
- Veli uygulaması (`web/parent`, React, ADR-0012) aynı token'ları kullanır: admin'deki `theme-tokens:start`/`theme-tokens:end` bloğu `web/parent/src/styles/tokens.css`'e birebir kopyalanır, CI'daki `scripts/check-theme-tokens.sh` sapmayı kırar (DEC-378). Token'lar yalnızca admin'de değiştirilir. Tema tercihi aynı `sa-theme` anahtarı ve `<html>` sınıflarıyla çalışır; Tailwind orada `@tailwindcss/vite` ile, ikonlar `lucide-react` ile.
- Karanlık mod: `<html>` üzerinde `dark` / `light` sınıfı; tercih `localStorage` (`sa-theme`: `light|dark|system`), yoksa `prefers-color-scheme`. İlk boyamadan önce küçük bir inline script uygular. `html.light` / `html.dark` ayrıca `color-scheme: light|dark` verir (form kontrolleri, kaydırma çubukları).
- Font: Inter (gövde), JetBrains Mono (sayılar, tablo başlıkları). Kök font boyutu `87.5%` (tarayıcı varsayılanı 16px iken 14px); sabit `px` verilmez, kullanıcının tarayıcı yazı boyutu ayarı korunur (NFR-USA). En küçük metin adımı `text-xs` = 12px (`--text-xs: calc(12rem / 14)`); `text-[10px]` / `text-[11px]` kullanılmaz.
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
| muted / muted-foreground | #F1F5F9 / #5B6B80 | #1E2736 / #A0AEC0 |
| accent / accent-foreground | #1D4ED8 / #FFFFFF | #60A5FA / #121620 |
| destructive / destructive-foreground | #C81E2B / #FFFFFF | #F87171 / #121620 |
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
| success / warning / info | #13733A / #A34C06 / #1D4ED8 | #4ADE80 / #F39C12 / #60A5FA |

### Kontrast (WCAG 2.1 AA, NFR-USA)

Normal ve küçük metin için en az 4.5:1 gerekir. Durum renkleri (`success`, `warning`, `destructive`, `info`) ve `muted-foreground` metin olarak da kullanıldığı için bu eşiği hem düz zeminde hem de rozet/uyarı kutularındaki `bg-X/10` tonu üzerinde geçmelidir. Oranlar sRGB göreli parlaklık formülüyle hesaplandı; `/10` tonu, rengin %10'unun zemine karıştırılmış hâlidir. Token değiştirilirse tablo yeniden hesaplanır.

| metin / zemin | light | dark |
|---|---|---|
| muted-foreground / background | 5.20 | 8.02 |
| muted-foreground / card | 5.44 | 7.23 |
| muted-foreground / muted | 4.97 | 6.66 |
| muted-foreground / tablo başlığı (`muted/60` card üstünde) | 5.16 | 6.91 |
| placeholder (`muted-foreground`, `card/50` input) | ≥ 5.20 | ≥ 7.23 |
| success / card · background | 5.93 · 5.67 | 9.36 · 10.38 |
| success / `success/10` (card · background üstünde) | 5.13 · 4.94 | 7.62 · 8.59 |
| warning / card · background | 5.84 · 5.59 | 7.44 · 8.24 |
| warning / `warning/10` (card · background üstünde) | 5.06 · 4.86 | 6.31 · 7.07 |
| destructive / card · background | 5.71 · 5.46 | 5.90 · 6.54 |
| destructive / `destructive/10` (card · background üstünde) | 4.87 · 4.64 | 5.14 · 5.75 |
| info / card · background | 6.70 · 6.41 | 6.42 · 7.11 |
| info / `info/10` (card · background üstünde) | 5.72 · 5.50 | 5.45 · 6.12 |
| destructive-foreground / destructive (`.btn-destructive`) | 5.71 | 6.54 |
| primary-foreground / primary | 6.70 | 7.11 |

Giriş sayfası tanıtım alanı: altbilgi `text-white/80`, `primary` (#1D4ED8) üzerinde 4.89 (gradyanın koyu ucunda 7.22). `text-white/60` (3.42) kullanılmaz.

Önceki değerler eşiği geçmiyordu: light `muted-foreground` #64748B `muted` üstünde 4.34; light `success` #16A34A / `warning` #D97706 / `destructive` #E63946 düz zeminde 3.0–4.2; dark `destructive` #E63946 card üstünde 3.92 ve üstündeki açık metin 3.70; dark `info` #3B82F6 `info/10` tonunda 3.92.

Radius: `--radius: 0.25rem`; `sm = r-2px`, `md = r`, `lg = r+2px`, `xl = r+4px`. Stat kartları `rounded-3xl`.

## Kabuk (layout)

- Kök: `h-screen flex overflow-hidden bg-background`.
- Sidebar: `fixed h-full border-r`, açık `w-64`, kapalı `w-20`; `lg` altında `bg-black/50` arka planlı çekmece. Üstte `h-16 px-4 border-b` logo alanı (`h-8 w-8 rounded-lg bg-primary/10 border border-primary/20`). Bölüm başlıkları `text-xs font-bold uppercase tracking-widest text-muted-foreground`.
- Menü öğesi: `flex h-9 items-center gap-2.5 px-3 rounded-lg text-sm font-medium`; aktif `bg-gradient-to-r from-primary/20 to-primary/5 text-primary border border-primary/30 shadow-lg shadow-primary/5`; pasif `text-muted-foreground hover:text-foreground hover:bg-muted/50`.
- Header: `sticky top-0 h-16 border-b border-border/50 bg-card/80 backdrop-blur-xl px-2 sm:px-6`; sağda tema düğmesi ve kullanıcı menüsü.
- İçerik: `flex-1 overflow-y-auto p-4 md:p-6`.
- Sayfa başlığı: `h1.text-xl.font-bold.tracking-tight` + `p.text-sm.text-muted-foreground`; aksiyonlar `flex flex-col md:flex-row md:items-center justify-between gap-4`.

## Bileşen kalıpları

- **Card:** `bg-card text-card-foreground rounded-xl border`, başlık `px-4 pt-4`, içerik `p-4`.
- **Table:** thead `bg-muted/60 border-b`; th `h-10 px-4 text-xs font-semibold uppercase tracking-wider font-mono text-muted-foreground`; satır `border-b border-border/60 hover:bg-muted/40`; td `px-4 py-3 text-sm`.
- **Button:** taban `inline-flex items-center gap-2 rounded-md text-sm font-semibold transition-all active:scale-95 focus-visible:ring-2 ring-primary/50 disabled:opacity-40`; default `bg-primary text-primary-foreground shadow-lg shadow-primary/10 hover:bg-primary/90`; outline `border-2 border-border text-muted-foreground hover:border-primary hover:text-primary hover:bg-primary/5`; secondary `bg-card border hover:bg-muted`; ghost `text-muted-foreground hover:text-primary hover:bg-primary/10`; destructive `bg-destructive text-destructive-foreground hover:bg-destructive/90`. Boyutlar `h-10 px-5`, sm `h-9 px-3 text-xs`, icon `size-10`.
- **Badge:** `rounded-full border px-2.5 py-0.5 text-xs font-bold uppercase tracking-wider`; renkli varyant `border-X/30 bg-X/10 text-X`.
- **Input:** `h-9 rounded-md border bg-card/50 px-3 placeholder:text-muted-foreground focus-visible:border-primary focus-visible:ring-[3px] focus-visible:ring-primary/20 aria-invalid:border-destructive`.
- **Dialog:** overlay `bg-slate-900/60`; içerik `bg-background rounded-lg border p-6 shadow-lg sm:max-w-lg`.
- **Tabs:** liste `bg-muted rounded-xl p-[3px]`; tetikleyici `rounded-xl px-3 py-2 text-sm font-medium`, aktif `bg-card`.
- **KPI kartı:** `p-4 rounded-3xl border bg-card shadow-sm hover:shadow-md relative overflow-hidden`, arkada `w-44 h-44 rounded-full blur-3xl opacity-[0.06]` renk parlaması; başlık `text-xs font-bold uppercase tracking-wider text-muted-foreground`; değer `text-2xl font-extrabold tracking-tight font-mono`.

## Grafik varsayılanları (ECharts, `app-chart`)

Şeffaf arka plan, Inter font, renkler `--chart-1..5` token'larından, eksen çizgi ve tikleri gizli, yatay ızgara çizgileri `--border` renginde kesikli, tooltip `--popover` renginde. Kullanılacak grafikler: devam oranı trendi (area), gruplara göre doluluk (bar), aylık tahsilat/borç (bar), yoklama dağılımı (donut), grup aylık yoklama tablosu (heatmap).
