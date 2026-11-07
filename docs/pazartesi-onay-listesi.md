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
| `AWSSDK.S3` 4.0.104.8 (→ `AWSSDK.Core` 4.0.102.14) | P2-F6 belge deposu istemcisi; sadece `Infrastructure/Storage` içinde, `IDocumentStorage` arkasında (DEC-320). Yayın 2026-10-29 / 2026-10-26 (7 günlük bekleme geçti) | Apache-2.0 | Minio .NET SDK (Apache-2.0, MinIO'ya bağlı geliştirme), FluentStorage (MIT, ek soyutlama katmanı), elle SigV4 imzalama |

## Araçlar (CI)
| Araç | Neden | Lisans | Sabitleme |
| --- | --- | --- | --- |
| SeaweedFS `chrislusf/seaweedfs:4.51` (compose `storage` servisi ve Testcontainers) | P2-F6: yerel geliştirme ve testlerde S3 uyumlu belge deposu (DEC-320). Sadece yerel/CI; production deposu Deploy bloğunda seçilir | Apache-2.0 | Digest ile (`compose.yaml`); yayın 2026-10-25 |
| gitleaks `v8.30.1` | NFR-SEC-09: repo geçmişinde gizli bilgi taraması, `repo-checks` işinde (DEC-200) | MIT | Resmi sürüm ikilisi + SHA-256 (`ci.yml`) |
| `e2e` CI işi (Playwright tarayıcıları `npx playwright install --with-deps`) | P1-36: kritik akışlar gerçek API + PostgreSQL'e karşı, beş proje (DEC-240) | Apache-2.0 | Tarayıcılar Playwright sürümüyle sabit; action'lar SHA'lı |

## Sahibe kalan işler
- **`.env` güncellemesi (P2-F6):** compose artık `STORAGE_ACCESS_KEY` ve `STORAGE_SECRET_KEY` istiyor; mevcut `.env`'e README'deki iki satırı ekleyin (agent `.env`'e dokunmadı). Eklenmeden `docker compose up` ve `scripts/e2e.sh` çalışmaz.
- `v0.0.1` ve `v0.1.0` etiketleri, faz geçişleri (P0-35, P1-40).
- Deploy bloğu.
- Öğrenme günlüğü notları (taslaklar `docs/ogrenme-gunlugu.md`'de).
- `e2e` CI işini zorunlu check yapmak (P1-36, DEC-240): ruleset bugün dört check istiyor (`backend`, `frontend`, `repo-checks`, `images`); `e2e` bilerek eklenmedi. Birkaç yeşil çalıştırmadan sonra ruleset'e eklenip eklenmeyeceğine sahip karar verir.
- Repository ruleset: `.github/CODEOWNERS` eklendi (CI, `.gitleaks.toml`, `docker/`, `scripts/`, `src/`, `tests/`, `docs/adr/`, paket dosyaları), ama ruleset değiştirilmedi. Code-owner incelemesini zorunlu yapmak sahibin kararı; zorunlu olursa hafta sonu otonom merge'leri (DEC-098) sahip onayı olmadan ilerleyemez. Not: sahip PR'ları kendisi açtığında GitHub kendi onayını code-owner onayı saymaz; ruleset'te bypass ya da ikinci bir inceleyici gerekir.

- GHCR: `publish` işi ilk günden beri yetki hatasıyla (write_package) kırılıyordu; imaj adları `spor-akademi-v2-*` yapıldı (DEC-260). Eski `spor-akademi-*` paketleri eski repoya bağlı görünüyor; onlara dokunulmadı. İlk başarılı push'tan sonra her paket (api, migrator, admin) için bir kerelik: Package settings → Change visibility → Public (DEC-089), ve "Manage Actions access" altında bu reponun yazma izni olduğunu kontrol edin.

## Sahibe sorular
- DEC-320: Production belge deposu ne olacak (ör. Cloudflare R2, Hetzner Object Storage, AWS S3, kendi sunucunda SeaweedFS)? Gerçek veri dönemi için KVKK yurt dışı aktarım koşulu (NFR-PRV-07) seçimi etkiler. Yerelde MinIO yerine SeaweedFS kullanılmasını (MinIO topluluk sürümünün arşivlenmesi, AGPL-3.0) onaylıyor musunuz?
- DEC-324: Sağlık raporlarını yalnız Okul Admini görüyor; antrenör sadece kendi grubundaki öğrencinin fotoğrafını görüyor. Antrenörün (ör. "spora uygundur" bilgisini görmek için) sağlık raporlarına erişmesi gerekir mi? Gerekirse rapor yerine sadece "rapor var / geçerlilik tarihi" gibi bir alan önerilir.
- DEC-322: Sınırlar uygun mu (rapor 5 MB, fotoğraf 2 MB, öğrenci başına 20 rapor)? Raporun geçerlilik tarihi/son kullanım takibi istenir mi?
- DEC-322: Fotoğraflardaki EXIF silindiği için telefon fotoğrafları yan görünebilir (yön bilgisi gider). Sunucuda görüntü işleme kütüphanesi (izinli lisanslı, ör. SkiaSharp MIT) eklenip fotoğraf döndürülerek yeniden kodlansın mı? Bu ayrıca virüs taraması (ClamAV, GPL-2.0, ayrı servis) ile birlikte değerlendirilebilir.
- DEC-151: Reşit değilken telefonsuz kaydedilen bir öğrenci 18'ine bastıktan sonra, herhangi bir bilgisi (ör. sadece adı) düzenlenmeden önce kendi telefonunun girilmesi gerekir. Bu kabul edilebilir mi, yoksa yetişkin telefon kuralı sadece telefon/veli değiştiğinde mi kontrol edilsin?
