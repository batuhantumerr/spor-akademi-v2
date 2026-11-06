# Pazartesi onay listesi

Sahip yokken (2026-11-06 – 2026-11-09) agent'ın aldığı ve sahibin onayını bekleyen kararlar ve işler. Her madde için: onayla, değiştir veya geri al.

## Kararlar
- DEC-099 – DEC-103 ve sonrası: [karar-gunlugu-v2.md](karar-gunlugu-v2.md) içindeki **Proposed** satırları.

## Paketler (gerekçe, lisans, alternatifler)
| Paket | Neden | Lisans | Alternatifler |
| --- | --- | --- | --- |
| (dokümandaki listeden: EF Core, Npgsql, EFCore.NamingConventions, xUnit v3, Shouldly, NSubstitute, Testcontainers, Respawn, FakeTimeProvider, NetArchTest, Coverlet) | Doküman kararları (DEC-023, DEC-061, DEC-079) | MIT / Apache-2.0 / BSD | — |
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

## Sahibe sorular
- DEC-151: Reşit değilken telefonsuz kaydedilen bir öğrenci 18'ine bastıktan sonra, herhangi bir bilgisi (ör. sadece adı) düzenlenmeden önce kendi telefonunun girilmesi gerekir. Bu kabul edilebilir mi, yoksa yetişkin telefon kuralı sadece telefon/veli değiştiğinde mi kontrol edilsin?
