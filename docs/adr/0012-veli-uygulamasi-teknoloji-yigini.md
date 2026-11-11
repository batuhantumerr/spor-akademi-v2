# ADR-0012 — Veli uygulaması teknoloji yığını

- **Durum:** Accepted (agent, sahip yetkisiyle 2026-11-11)
- **İlgili:** P3-04, DEC-024, DEC-377 – DEC-380, [ADR-0010](0010-ui-kutuphanesi.md), [ui-tema.md](../ui-tema.md)

## Bağlam

DEC-024 veli uygulamasının React ile yazılmasını sabitler. Veliler uygulamayı çoğunlukla telefondan açar (önce mobil, P3-06), sonra kurulabilir bir PWA olur (P3-07). Admin uygulamasıyla aynı görsel dil (tema token'ları, açık/koyu tema) ve aynı API sözleşmesi (OpenAPI belgesi) kullanılmalı, ama iki uygulamanın build'leri birbirine bağlanmamalı: biri kırılınca diğeri yayımlanabilmeli. Ticari lisanslı paket yasak (bölüm 9); sürümler 7 günlük cooldown'a uyar (DEC-080).

## Değerlendirilen seçenekler

| Konu | Seçenekler | Seçim ve gerekçe |
| --- | --- | --- |
| Build | Vite, Next.js, Create React App | **Vite.** Statik SPA yeterli (SSR gerekmiyor, API ayrı); nginx'te admin gibi sunulur. Next.js sunucu çalışma zamanı ister, CRA bakımda değil. |
| Yönlendirme | React Router, TanStack Router | **React Router 8** (data router, `createBrowserRouter`). En yaygın, testte `createMemoryRouter` ile aynı rota tablosu kullanılır. TanStack Router tip güvenliği güçlü ama ekip ve ekosistem daha küçük. |
| Sunucu durumu | TanStack Query, SWR, elle `fetch` | **TanStack Query 5.** Önbellek, yeniden deneme, odakta tazeleme ve mutasyonlar tek yerde; P3-08'de SignalR bildirimiyle `invalidateQueries` doğal eşleşir. |
| API istemcisi | ng-openapi-gen (admin), openapi-typescript + openapi-fetch, @hey-api/openapi-ts, orval | **openapi-typescript (yalnızca tip üretir) + openapi-fetch (~6 kB çalışma zamanı).** İkisi de MIT. Üretilen dosya sadece tip olduğu için pakete kod eklemez; yol, parametre ve gövde tipleri sözleşmeden gelir, sözleşme değişince `typecheck` kırılır. hey-api ve orval daha fazla kod üretir ve 0.x/sık kırılan sürümlerde. |
| Stil | Tailwind v4, CSS modules, bileşen kütüphanesi | **Tailwind v4** (`@tailwindcss/vite`), admin'in token'larının birebir kopyası (DEC-378). Bileşen sınıfları (`.btn`, `.card`) admin'deki gibi. |
| Test | Vitest + Testing Library, Jest | **Vitest + Testing Library + jsdom.** Vite yapılandırmasını paylaşır; admin de Vitest kullanıyor. |
| Lint | ESLint flat config, Biome | **ESLint 10 + typescript-eslint `strictTypeChecked`** + `eslint-plugin-react-hooks` + `eslint-plugin-react-refresh`, **Prettier.** Admin'le aynı araçlar. |

## Karar

- `web/parent`: React 19.3, Vite 8.3, TypeScript 6.0 (`strict`, `noUncheckedIndexedAccess`, `exactOptionalPropertyTypes`), React Router 8.5, TanStack Query 5.105, openapi-typescript 7.13 + openapi-fetch 0.17, Tailwind 4.3, lucide-react (ikon), Inter (`@fontsource-variable`, kendi sunucumuzdan).
- Test ve araçlar: Vitest 5.1, Testing Library (react, dom, jest-dom, user-event), jsdom 30, ESLint 10, typescript-eslint 8.72, Prettier 3.9.
- Bütün sürümler `package.json`'da sabit (aralık yok); kilit dosyası `npm install --before=<7 gün önce>` ile kuruldu, böylece dolaylı bağımlılıklar da cooldown'a uyar. Ayrıntı ve lisanslar DEC-377'de.
- Türkçe metinler `src/i18n/tr.ts` sözlüğünde; bileşenlerde metin yazılmaz (P3-10 İngilizce sözlüğü ekler).
- Oturum (P3-05) admin'deki modeli izler (ADR-0006): access token bellekte, refresh token HttpOnly çerezde; bu ADR kapsamında değil.

## Sonuçlar

- İki SPA iki ayrı kilit dosyası, iki ayrı imaj ve iki ayrı CI işi demek; ortak kod paylaşımı yok, ortak olan yalnızca tema token'ları ve OpenAPI belgesi (ikisi de kopya + CI kontrolü, DEC-378, DEC-379).
- openapi-typescript 7.13'ün `typescript` peer aralığı `^5.x`; TypeScript 6.0 ile çalıştığı doğrulandı ve `overrides` ile kendi TypeScript'imize bağlandı. openapi-typescript TypeScript 6'yı resmi olarak desteklediğinde override kaldırılır.
- typescript-eslint TypeScript `< 6.1` destekliyor; Dependabot TypeScript'in minor/major güncellemelerini açmaz, ikisi birlikte elle yükseltilir.
- React Router, React, Vite ve Vitest major sürümleri Dependabot'ta yoksayılır; planlı yükseltmedir.
