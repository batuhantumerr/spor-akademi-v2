# Pazartesi onay listesi

Sahip yokken (2026-11-06 – 2026-11-09) agent'ın aldığı ve sahibin onayını bekleyen kararlar ve işler. Her madde için: onayla, değiştir veya geri al.

## Kararlar
- DEC-099 – DEC-103 ve sonrası: [karar-gunlugu-v2.md](karar-gunlugu-v2.md) içindeki **Proposed** satırları.
- DEC-300 – DEC-304 (P2-F3 transactional outbox): yeni paket eklenmedi; `BackgroundService`, health check ve System.Text.Json paylaşılan ASP.NET Core çerçevesinden, `Decorate` mevcut Scrutor'dan geliyor.
- DEC-310 – DEC-316 (P2-F7 cache): Redis yerine Valkey (lisans), neyin cache'lendiği ve neyin cache'lenmediği (kişisel veri yok), commit sonrası otomatik invalidation, isteğe bağlı L2. Valkey compose'un `cache` profilinde ve isteğe bağlı; `VALKEY_PASSWORD` olmayan eski `.env` hiçbir şeyi bozmaz. Valkey'i kullanmak için bir kez `bash scripts/setup-env.sh` (sadece eksik `VALKEY_*` anahtarlarını ekler) ve `docker compose --profile cache up -d --wait`.
- DEC-330 – DEC-335 (P2-F2 Hangfire ve iş paneli): paketler aşağıda. Onay bekleyen seçimler: şemanın migrator migration'ıyla paketin kendi script'lerinden kurulması (DEC-331), panel erişiminin kısa ömürlü çerezle olması ve varsayılan salt okunur panel (DEC-332), outbox'un varsayılan olarak Hangfire'a geçmesi ve `Outbox:Enabled` yerine `Outbox:Scheduler` (DEC-333), refresh token temizlik işi (DEC-334).
- DEC-360 – DEC-367 (P2-F5 otomatik anonimleştirme): yeni paket yok. Onay bekleyen seçimler: varsayılan saklama süresi ve sınırlar (DEC-360, DEC-361), nelerin silinip nelerin kaldığı (cinsiyet kalıyor, DEC-363), başka öğrencisi kalmayan velinin tamamen silinmesi (DEC-365), ödemelerin yasal süre sonunda henüz silinmemesi (DEC-367).

## Paketler (gerekçe, lisans, alternatifler)
| Paket | Neden | Lisans | Alternatifler |
| --- | --- | --- | --- |
| (dokümandaki listeden: EF Core, Npgsql, EFCore.NamingConventions, xUnit v3, Shouldly, NSubstitute, Testcontainers, Respawn, FakeTimeProvider, NetArchTest, Coverlet) | Doküman kararları (DEC-023, DEC-061, DEC-079) | MIT / Apache-2.0 / BSD | — |
| `Hangfire.Core`, `Hangfire.NetCore`, `Hangfire.AspNetCore` 1.8.25 (yayın 2026-08-28) | P2-F2 arka plan işleri ve iş paneli; doküman Hangfire'ı adıyla istiyor (DEC-330). Hangfire Pro kullanılmıyor | LGPL-3.0 (çoklu lisans; değiştirilmeden, NuGet ile dinamik bağlama) | Quartz.NET (Apache-2.0, panel yok), kendi `BackgroundService` döngüleri |
| `Hangfire.PostgreSql` 1.21.1 (yayın 2026-02-11; geçişli `Dapper` 2.0.123, `Dapper.AOT` 1.0.48, Apache-2.0) | Hangfire'ın PostgreSQL deposu, `hangfire` şeması (DEC-331) | LGPL-3.0 | `Hangfire.Pro.Redis` (ticari, yasak), Redis (P2-F7'den önce yeni altyapı) |
| `Newtonsoft.Json` 13.0.4 (merkezi geçişli sabitleme) | `Hangfire.Core`'un istediği 11.0.1 GHSA-5crp-9r3c-p9vr açığını taşıyor | MIT | — |
| `@playwright/test` 1.64.1 (+ `playwright-core` 1.64.1 `overrides` ile) | P1-36 uçtan uca testler, NFR-CMP-01 (Chromium/Firefox/WebKit); dokümanın test araçları listesinde (5.8). Dev bağımlılığı (DEC-240) | Apache-2.0 | Cypress (WebKit yok), WebdriverIO |
| `@axe-core/playwright` 4.14.0 (→ `axe-core` 4.14.0) | NFR-USA-02: e2e'de otomatik axe kontrolü. Dev bağımlılığı, dağıtılmaz (DEC-240) | MPL-2.0 (zayıf copyleft, ticari değil) | Pa11y (axe'i sarar), Lighthouse CI (sayfa başına, akış içinde değil); reddedilirse axe adımları kaldırılır |
| `Microsoft.Extensions.Caching.Hybrid` 10.11.0 (2026-10-14) | P2-F7: L1 + L2 cache, etiketle invalidation, stampede koruması (DEC-310) | MIT | `IMemoryCache` + `IDistributedCache` elle (stampede koruması ve etiket yok), FusionCache (MIT, üçüncü taraf) |
| `Microsoft.Extensions.Caching.StackExchangeRedis` 10.0.14 (2026-10-25) | HybridCache'in L2'si için Redis protokolüyle `IDistributedCache` (Valkey ile çalışır) | MIT | Kendi `IDistributedCache` uygulaması |
| `StackExchange.Redis` 2.13.17 (2026-05-27) | Redis/Valkey istemcisi; doğrudan referans `AbortOnConnectFail`/`BacklogPolicy` ayarları için. 3.x değil: üstteki paket 2.x'e karşı derlenmiş; 2.13.23 7 günlük bekleme süresinde | MIT (bağımlılıkları `Pipelines.Sockets.Unofficial` ve `System.IO.Hashing` de MIT) | NRedisStack (MIT, aynı çekirdek), Valkey GLIDE (Apache-2.0, .NET istemcisi henüz olgun değil) |
| `AWSSDK.S3` 4.0.104.8 (→ `AWSSDK.Core` 4.0.102.14) | P2-F6 belge deposu istemcisi; sadece `Infrastructure/Storage` içinde, `IDocumentStorage` arkasında (DEC-320). Yayın 2026-10-29 / 2026-10-26 (7 günlük bekleme geçti) | Apache-2.0 | Minio .NET SDK (Apache-2.0, MinIO'ya bağlı geliştirme), FluentStorage (MIT, ek soyutlama katmanı), elle SigV4 imzalama |

## Araçlar (CI)
| Araç | Neden | Lisans | Sabitleme |
| --- | --- | --- | --- |
| SeaweedFS `chrislusf/seaweedfs:4.51` (compose `storage` servisi ve Testcontainers) | P2-F6: yerel geliştirme ve testlerde S3 uyumlu belge deposu (DEC-320). Sadece yerel/CI; production deposu Deploy bloğunda seçilir | Apache-2.0 | Digest ile (`compose.yaml`); yayın 2026-10-25 |
| gitleaks `v8.30.1` | NFR-SEC-09: repo geçmişinde gizli bilgi taraması, `repo-checks` işinde (DEC-200) | MIT | Resmi sürüm ikilisi + SHA-256 (`ci.yml`) |
| `e2e` CI işi (Playwright tarayıcıları `npx playwright install --with-deps`) | P1-36: kritik akışlar gerçek API + PostgreSQL'e karşı, beş proje (DEC-240) | Apache-2.0 | Tarayıcılar Playwright sürümüyle sabit; action'lar SHA'lı |
| Valkey imajı `valkey/valkey:9.1.3-alpine` (2026-10-27) | P2-F7 L2 cache sunucusu: yerel compose ve Testcontainers (DEC-310, DEC-316). Redis ≥ 7.4 izinli lisanslı değil (RSAL/SSPL/AGPL) | BSD-3-Clause | Tag + digest (`compose.yaml`) |

## Sahibe kalan işler
- **Belge deposu (P2-F6), isteğe bağlı:** `.env`'e elle bir şey eklemek gerekmez. Depoyu kullanmak isterseniz `bash scripts/setup-env.sh` (sadece eksik `STORAGE_*` anahtarlarını ekler) ve `docker compose --profile storage up -d --wait` (DEC-326).
- `v0.0.1` ve `v0.1.0` etiketleri, faz geçişleri (P0-35, P1-40).
- Deploy bloğu.
- Öğrenme günlüğü notları (taslaklar `docs/ogrenme-gunlugu.md`'de).
- `e2e` CI işini zorunlu check yapmak (P1-36, DEC-240): ruleset bugün dört check istiyor (`backend`, `frontend`, `repo-checks`, `images`); `e2e` bilerek eklenmedi. Birkaç yeşil çalıştırmadan sonra ruleset'e eklenip eklenmeyeceğine sahip karar verir.
- Repository ruleset: `.github/CODEOWNERS` eklendi (CI, `.gitleaks.toml`, `docker/`, `scripts/`, `src/`, `tests/`, `docs/adr/`, paket dosyaları), ama ruleset değiştirilmedi. Code-owner incelemesini zorunlu yapmak sahibin kararı; zorunlu olursa hafta sonu otonom merge'leri (DEC-098) sahip onayı olmadan ilerleyemez. Not: sahip PR'ları kendisi açtığında GitHub kendi onayını code-owner onayı saymaz; ruleset'te bypass ya da ikinci bir inceleyici gerekir.

- GHCR: `publish` işi ilk günden beri yetki hatasıyla (write_package) kırılıyordu; imaj adları `spor-akademi-v2-*` yapıldı (DEC-260). Eski `spor-akademi-*` paketleri eski repoya bağlı görünüyor; onlara dokunulmadı. İlk başarılı push'tan sonra her paket (api, migrator, admin) için bir kerelik: Package settings → Change visibility → Public (DEC-089), ve "Manage Actions access" altında bu reponun yazma izni olduğunu kontrol edin.

- İş paneli (DEC-332) için Deploy bloğunda: reverse proxy `/jobs`'u API'ye yönlendirmeli; panel oturumları süreç belleğinde olduğu için birden çok API örneğinde yapışkan oturum veya paylaşılan bir oturum deposu gerekir.

## Sahibe sorular
- **DEC-360 (sahip ve hukuki onay gerekli):** Ayrılan öğrencinin kişisel verisi için varsayılan saklama süresi **24 ay**, okulun seçebileceği aralık **6–120 ay** uygun mu? Ödeme kayıtları için **10 yıl** (TTK md. 82; VUK 5 yıl) doğru yasal süre mi? Bu süreler aydınlatma metnine (NFR-PRV-06) de yazılmalı; gerçek veri döneminden (Faz 4) önce bir hukukçuya teyit ettirilmesi önerilir.
- DEC-363: Anonimleştirmede **cinsiyet** istatistik için tutuluyor; ad, doğum tarihi, iletişim, acil kişi, veliler ve belgeler siliniyor. Cinsiyet de silinsin mi?
- DEC-367: Ödeme iptal gerekçesi serbest metin ve kişi adı içerebilir; ödeme kayıtları değişmez olduğu için anonimleştirme onu silmiyor. Anonimleştirmede gerekçe silinsin mi (değişmezlik kuralına istisna), yoksa arayüzde "gerekçeye kişi adı yazmayın" uyarısı mı yeterli? Ödemeler 10 yıl sonunda silinsin mi, anonim kalsın mı?
- DEC-320: Production belge deposu ne olacak (ör. Cloudflare R2, Hetzner Object Storage, AWS S3, kendi sunucunda SeaweedFS)? Gerçek veri dönemi için KVKK yurt dışı aktarım koşulu (NFR-PRV-07) seçimi etkiler. Yerelde MinIO yerine SeaweedFS kullanılmasını (MinIO topluluk sürümünün arşivlenmesi, AGPL-3.0) onaylıyor musunuz?
- DEC-324: Sağlık raporlarını yalnız Okul Admini görüyor; antrenör sadece kendi grubundaki öğrencinin fotoğrafını görüyor. Antrenörün (ör. "spora uygundur" bilgisini görmek için) sağlık raporlarına erişmesi gerekir mi? Gerekirse rapor yerine sadece "rapor var / geçerlilik tarihi" gibi bir alan önerilir.
- DEC-322: Sınırlar uygun mu (rapor 5 MB, fotoğraf 2 MB, öğrenci başına 20 rapor)? Raporun geçerlilik tarihi/son kullanım takibi istenir mi?
- DEC-322: Fotoğraflardaki EXIF silindiği için telefon fotoğrafları yan görünebilir (yön bilgisi gider). Sunucuda görüntü işleme kütüphanesi (izinli lisanslı, ör. SkiaSharp MIT) eklenip fotoğraf döndürülerek yeniden kodlansın mı? Bu ayrıca virüs taraması (ClamAV, GPL-2.0, ayrı servis) ile birlikte değerlendirilebilir.
- DEC-151: Reşit değilken telefonsuz kaydedilen bir öğrenci 18'ine bastıktan sonra, herhangi bir bilgisi (ör. sadece adı) düzenlenmeden önce kendi telefonunun girilmesi gerekir. Bu kabul edilebilir mi, yoksa yetişkin telefon kuralı sadece telefon/veli değiştiğinde mi kontrol edilsin?
