# Pazartesi onay listesi

Sahip yokken (2026-11-06 – 2026-11-09) agent'ın aldığı ve sahibin onayını bekleyen kararlar ve işler. Her madde için: onayla, değiştir veya geri al.

## Kararlar
- DEC-099 – DEC-103 ve sonrası: [karar-gunlugu-v2.md](karar-gunlugu-v2.md) içindeki **Proposed** satırları.
- DEC-300 – DEC-304 (P2-F3 transactional outbox): yeni paket eklenmedi; `BackgroundService`, health check ve System.Text.Json paylaşılan ASP.NET Core çerçevesinden, `Decorate` mevcut Scrutor'dan geliyor.
- DEC-310 – DEC-316 (P2-F7 cache): Redis yerine Valkey (lisans), neyin cache'lendiği ve neyin cache'lenmediği (kişisel veri yok), commit sonrası otomatik invalidation, isteğe bağlı L2. Valkey compose'un `cache` profilinde ve isteğe bağlı; `VALKEY_PASSWORD` olmayan eski `.env` hiçbir şeyi bozmaz. Valkey'i kullanmak için bir kez `bash scripts/setup-env.sh` (sadece eksik `VALKEY_*` anahtarlarını ekler) ve `docker compose --profile cache up -d --wait`.

## Paketler (gerekçe, lisans, alternatifler)
| Paket | Neden | Lisans | Alternatifler |
| --- | --- | --- | --- |
| (dokümandaki listeden: EF Core, Npgsql, EFCore.NamingConventions, xUnit v3, Shouldly, NSubstitute, Testcontainers, Respawn, FakeTimeProvider, NetArchTest, Coverlet) | Doküman kararları (DEC-023, DEC-061, DEC-079) | MIT / Apache-2.0 / BSD | — |
| `@playwright/test` 1.64.1 (+ `playwright-core` 1.64.1 `overrides` ile) | P1-36 uçtan uca testler, NFR-CMP-01 (Chromium/Firefox/WebKit); dokümanın test araçları listesinde (5.8). Dev bağımlılığı (DEC-240) | Apache-2.0 | Cypress (WebKit yok), WebdriverIO |
| `@axe-core/playwright` 4.14.0 (→ `axe-core` 4.14.0) | NFR-USA-02: e2e'de otomatik axe kontrolü. Dev bağımlılığı, dağıtılmaz (DEC-240) | MPL-2.0 (zayıf copyleft, ticari değil) | Pa11y (axe'i sarar), Lighthouse CI (sayfa başına, akış içinde değil); reddedilirse axe adımları kaldırılır |
| `Microsoft.Extensions.Caching.Hybrid` 10.11.0 (2026-10-14) | P2-F7: L1 + L2 cache, etiketle invalidation, stampede koruması (DEC-310) | MIT | `IMemoryCache` + `IDistributedCache` elle (stampede koruması ve etiket yok), FusionCache (MIT, üçüncü taraf) |
| `Microsoft.Extensions.Caching.StackExchangeRedis` 10.0.14 (2026-10-25) | HybridCache'in L2'si için Redis protokolüyle `IDistributedCache` (Valkey ile çalışır) | MIT | Kendi `IDistributedCache` uygulaması |
| `StackExchange.Redis` 2.13.17 (2026-05-27) | Redis/Valkey istemcisi; doğrudan referans `AbortOnConnectFail`/`BacklogPolicy` ayarları için. 3.x değil: üstteki paket 2.x'e karşı derlenmiş; 2.13.23 7 günlük bekleme süresinde | MIT (bağımlılıkları `Pipelines.Sockets.Unofficial` ve `System.IO.Hashing` de MIT) | NRedisStack (MIT, aynı çekirdek), Valkey GLIDE (Apache-2.0, .NET istemcisi henüz olgun değil) |

## Araçlar (CI)
| Araç | Neden | Lisans | Sabitleme |
| --- | --- | --- | --- |
| gitleaks `v8.30.1` | NFR-SEC-09: repo geçmişinde gizli bilgi taraması, `repo-checks` işinde (DEC-200) | MIT | Resmi sürüm ikilisi + SHA-256 (`ci.yml`) |
| `e2e` CI işi (Playwright tarayıcıları `npx playwright install --with-deps`) | P1-36: kritik akışlar gerçek API + PostgreSQL'e karşı, beş proje (DEC-240) | Apache-2.0 | Tarayıcılar Playwright sürümüyle sabit; action'lar SHA'lı |
| Valkey imajı `valkey/valkey:9.1.3-alpine` (2026-10-27) | P2-F7 L2 cache sunucusu: yerel compose ve Testcontainers (DEC-310, DEC-316). Redis ≥ 7.4 izinli lisanslı değil (RSAL/SSPL/AGPL) | BSD-3-Clause | Tag + digest (`compose.yaml`) |

## Sahibe kalan işler
- `v0.0.1` ve `v0.1.0` etiketleri, faz geçişleri (P0-35, P1-40).
- Deploy bloğu.
- Öğrenme günlüğü notları (taslaklar `docs/ogrenme-gunlugu.md`'de).
- `e2e` CI işini zorunlu check yapmak (P1-36, DEC-240): ruleset bugün dört check istiyor (`backend`, `frontend`, `repo-checks`, `images`); `e2e` bilerek eklenmedi. Birkaç yeşil çalıştırmadan sonra ruleset'e eklenip eklenmeyeceğine sahip karar verir.
- Repository ruleset: `.github/CODEOWNERS` eklendi (CI, `.gitleaks.toml`, `docker/`, `scripts/`, `src/`, `tests/`, `docs/adr/`, paket dosyaları), ama ruleset değiştirilmedi. Code-owner incelemesini zorunlu yapmak sahibin kararı; zorunlu olursa hafta sonu otonom merge'leri (DEC-098) sahip onayı olmadan ilerleyemez. Not: sahip PR'ları kendisi açtığında GitHub kendi onayını code-owner onayı saymaz; ruleset'te bypass ya da ikinci bir inceleyici gerekir.

- GHCR: `publish` işi ilk günden beri yetki hatasıyla (write_package) kırılıyordu; imaj adları `spor-akademi-v2-*` yapıldı (DEC-260). Eski `spor-akademi-*` paketleri eski repoya bağlı görünüyor; onlara dokunulmadı. İlk başarılı push'tan sonra her paket (api, migrator, admin) için bir kerelik: Package settings → Change visibility → Public (DEC-089), ve "Manage Actions access" altında bu reponun yazma izni olduğunu kontrol edin.

## Sahibe sorular
- DEC-151: Reşit değilken telefonsuz kaydedilen bir öğrenci 18'ine bastıktan sonra, herhangi bir bilgisi (ör. sadece adı) düzenlenmeden önce kendi telefonunun girilmesi gerekir. Bu kabul edilebilir mi, yoksa yetişkin telefon kuralı sadece telefon/veli değiştiğinde mi kontrol edilsin?
