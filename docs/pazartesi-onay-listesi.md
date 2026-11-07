# Pazartesi onay listesi

Sahip yokken (2026-11-06 – 2026-11-09) agent'ın aldığı ve sahibin onayını bekleyen kararlar ve işler. Her madde için: onayla, değiştir veya geri al.

## Kararlar
- DEC-099 – DEC-103 ve sonrası: [karar-gunlugu-v2.md](karar-gunlugu-v2.md) içindeki **Proposed** satırları.
- DEC-300 – DEC-304 (P2-F3 transactional outbox): yeni paket eklenmedi; `BackgroundService`, health check ve System.Text.Json paylaşılan ASP.NET Core çerçevesinden, `Decorate` mevcut Scrutor'dan geliyor.
- DEC-330 – DEC-335 (P2-F2 Hangfire ve iş paneli): paketler aşağıda. Onay bekleyen seçimler: şemanın migrator migration'ıyla paketin kendi script'lerinden kurulması (DEC-331), panel erişiminin kısa ömürlü çerezle olması ve varsayılan salt okunur panel (DEC-332), outbox'un varsayılan olarak Hangfire'a geçmesi ve `Outbox:Enabled` yerine `Outbox:Scheduler` (DEC-333), refresh token temizlik işi (DEC-334).

## Paketler (gerekçe, lisans, alternatifler)
| Paket | Neden | Lisans | Alternatifler |
| --- | --- | --- | --- |
| (dokümandaki listeden: EF Core, Npgsql, EFCore.NamingConventions, xUnit v3, Shouldly, NSubstitute, Testcontainers, Respawn, FakeTimeProvider, NetArchTest, Coverlet) | Doküman kararları (DEC-023, DEC-061, DEC-079) | MIT / Apache-2.0 / BSD | — |
| `Hangfire.Core`, `Hangfire.NetCore`, `Hangfire.AspNetCore` 1.8.25 (yayın 2026-08-28) | P2-F2 arka plan işleri ve iş paneli; doküman Hangfire'ı adıyla istiyor (DEC-330). Hangfire Pro kullanılmıyor | LGPL-3.0 (çoklu lisans; değiştirilmeden, NuGet ile dinamik bağlama) | Quartz.NET (Apache-2.0, panel yok), kendi `BackgroundService` döngüleri |
| `Hangfire.PostgreSql` 1.21.1 (yayın 2026-02-11; geçişli `Dapper` 2.0.123, `Dapper.AOT` 1.0.48, Apache-2.0) | Hangfire'ın PostgreSQL deposu, `hangfire` şeması (DEC-331) | LGPL-3.0 | `Hangfire.Pro.Redis` (ticari, yasak), Redis (P2-F7'den önce yeni altyapı) |
| `Newtonsoft.Json` 13.0.4 (merkezi geçişli sabitleme) | `Hangfire.Core`'un istediği 11.0.1 GHSA-5crp-9r3c-p9vr açığını taşıyor | MIT | — |
| `@playwright/test` 1.64.1 (+ `playwright-core` 1.64.1 `overrides` ile) | P1-36 uçtan uca testler, NFR-CMP-01 (Chromium/Firefox/WebKit); dokümanın test araçları listesinde (5.8). Dev bağımlılığı (DEC-240) | Apache-2.0 | Cypress (WebKit yok), WebdriverIO |
| `@axe-core/playwright` 4.14.0 (→ `axe-core` 4.14.0) | NFR-USA-02: e2e'de otomatik axe kontrolü. Dev bağımlılığı, dağıtılmaz (DEC-240) | MPL-2.0 (zayıf copyleft, ticari değil) | Pa11y (axe'i sarar), Lighthouse CI (sayfa başına, akış içinde değil); reddedilirse axe adımları kaldırılır |

## Araçlar (CI)
| Araç | Neden | Lisans | Sabitleme |
| --- | --- | --- | --- |
| gitleaks `v8.30.1` | NFR-SEC-09: repo geçmişinde gizli bilgi taraması, `repo-checks` işinde (DEC-200) | MIT | Resmi sürüm ikilisi + SHA-256 (`ci.yml`) |
| `e2e` CI işi (Playwright tarayıcıları `npx playwright install --with-deps`) | P1-36: kritik akışlar gerçek API + PostgreSQL'e karşı, beş proje (DEC-240) | Apache-2.0 | Tarayıcılar Playwright sürümüyle sabit; action'lar SHA'lı |

## Sahibe kalan işler
- `v0.0.1` ve `v0.1.0` etiketleri, faz geçişleri (P0-35, P1-40).
- Deploy bloğu.
- Öğrenme günlüğü notları (taslaklar `docs/ogrenme-gunlugu.md`'de).
- `e2e` CI işini zorunlu check yapmak (P1-36, DEC-240): ruleset bugün dört check istiyor (`backend`, `frontend`, `repo-checks`, `images`); `e2e` bilerek eklenmedi. Birkaç yeşil çalıştırmadan sonra ruleset'e eklenip eklenmeyeceğine sahip karar verir.
- Repository ruleset: `.github/CODEOWNERS` eklendi (CI, `.gitleaks.toml`, `docker/`, `scripts/`, `src/`, `tests/`, `docs/adr/`, paket dosyaları), ama ruleset değiştirilmedi. Code-owner incelemesini zorunlu yapmak sahibin kararı; zorunlu olursa hafta sonu otonom merge'leri (DEC-098) sahip onayı olmadan ilerleyemez. Not: sahip PR'ları kendisi açtığında GitHub kendi onayını code-owner onayı saymaz; ruleset'te bypass ya da ikinci bir inceleyici gerekir.

- GHCR: `publish` işi ilk günden beri yetki hatasıyla (write_package) kırılıyordu; imaj adları `spor-akademi-v2-*` yapıldı (DEC-260). Eski `spor-akademi-*` paketleri eski repoya bağlı görünüyor; onlara dokunulmadı. İlk başarılı push'tan sonra her paket (api, migrator, admin) için bir kerelik: Package settings → Change visibility → Public (DEC-089), ve "Manage Actions access" altında bu reponun yazma izni olduğunu kontrol edin.

- İş paneli (DEC-332) için Deploy bloğunda: reverse proxy `/jobs`'u API'ye yönlendirmeli; panel oturumları süreç belleğinde olduğu için birden çok API örneğinde yapışkan oturum veya paylaşılan bir oturum deposu gerekir.

## Sahibe sorular
- DEC-151: Reşit değilken telefonsuz kaydedilen bir öğrenci 18'ine bastıktan sonra, herhangi bir bilgisi (ör. sadece adı) düzenlenmeden önce kendi telefonunun girilmesi gerekir. Bu kabul edilebilir mi, yoksa yetişkin telefon kuralı sadece telefon/veli değiştiğinde mi kontrol edilsin?
