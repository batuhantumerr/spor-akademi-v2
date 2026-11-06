# ADR-0010 — Admin arayüzü UI kütüphanesi

- **Durum:** Proposed (agent, 2026-11-06; sahibin onayı bekleniyor)
- **İlgili:** P1-13, DEC-103, DEC-112, [ui-tema.md](../ui-tema.md)

## Bağlam

Admin arayüzü Angular 22 ile yazılıyor (DEC-024). Antrenör ekranları mobil öncelikli (NFR-USA-01), temel erişilebilirlik isteniyor (NFR-USA-02) ve sahip, arayüzün modern, anlaşılır ve grafiklerle desteklenmiş olmasını, kendi başka projesindeki (React + Tailwind + Radix/shadcn) görsel dile benzemesini istedi. Ticari lisanslı paketler yasak (bölüm 9).

## Değerlendirilen seçenekler

| Seçenek | Artı | Eksi |
| --- | --- | --- |
| Angular Material | Olgun, erişilebilir, Google destekli | Material görünümünü değiştirmek zor; istenen görsel dile uzak |
| PrimeNG | Çok bileşen (tablo, takvim) | Tema sistemi ağır; görünüm istenen dilden farklı; büyük paket |
| Spartan UI (shadcn Angular) | shadcn görünümü | Hâlâ hızlı değişen, sürüm kararlılığı düşük |
| **Tailwind CSS v4 + Angular CDK + lucide** | Tasarım token'ları tamamen bizde; istenen görsel dil birebir uygulanır; CDK erişilebilir overlay/dialog/a11y altyapısı verir; küçük paket | Bileşenleri (buton, tablo, form alanı) kendimiz yazarız |

Grafik için: **ApexCharts** (sahibin diğer projesindeki ısı haritası) artık çift lisanslı; 2M USD üzeri gelirde ve başkalarının kullandığı ürünlerde ücretli lisans istiyor → **reddedildi** (ticari lisans yasağı). Chart.js (MIT) hafif ama ısı haritası eklentisi gerekiyor. **Apache ECharts (Apache-2.0) + ngx-echarts (MIT)** bar, çizgi, donut ve ısı haritasını tek kütüphanede veriyor; `echarts/core` ile sadece kullanılan grafikler pakete girer.

## Karar

- Stil: **Tailwind CSS v4** (`@tailwindcss/postcss`), token'lar `src/styles.css` içinde CSS değişkenleri; açık/koyu tema `<html>` sınıfı.
- Davranış ve erişilebilirlik: **Angular CDK** (dialog, overlay, focus trap, a11y).
- İkonlar: **@lucide/angular** (ISC).
- Grafikler: **ECharts + ngx-echarts**, tema token'larından renk okuyan ortak `app-chart` bileşeni.
- Fontlar: Inter ve JetBrains Mono, `@fontsource-variable` ile kendi sunucumuzdan (dış CDN yok).

## Sonuçlar

- Buton, kart, tablo, rozet, form alanı sınıfları (`.btn`, `.card`, `.data-table`…) ve küçük bileşenler (`app-page-header`, `app-stat-card`, `app-chart`) bizim bakımımızda.
- ECharts paketi büyük; sadece grafik içeren sayfalarda tembel yüklenir.
- Tüm paketler MIT / ISC / Apache-2.0 / OFL-1.1; sürümler 7 günlük cooldown'a uyar.
