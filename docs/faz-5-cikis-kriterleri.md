# Faz 5 çıkış kriterleri: doğrulama

Faz 5 (`v1.0.0`, P5-01 – P5-07) çıkış kriterlerinin ([roadmap](roadmap.md), P5-R1) neyle kanıtlandığı. Entegrasyon testleri `backend` işinde (Testcontainers, gerçek PostgreSQL ve RabbitMQ), uçtan uca testler `e2e` işinde, altyapı kodu denetimi `repo-checks` işinde, imaj ve duman testleri `images` işinde her PR'da çalışır. Aşağıdaki sayılar 2026-11-18'de yerelde (Release, Windows, Docker) koşturularak alındı. Son tamamlanmış `main` CI koşusu (`1c5a569`) tüm işleriyle yeşildir (`backend`, `frontend`, `parent`, `repo-checks`, `images`, `e2e`, `a11y`, `publish`) ve P5-01 – P5-06'yı kapsar; ondan sonraki P5-07 yalnız dokümandır. **Bulut hesabı yok: Azure'da hiçbir şey çalıştırılmadı.** Faz geçişi (P5-R2) ve `v1.0.0` etiketi sahiptedir.

## Yerelde koşturulan doğrulama

| Proje | Sınıflar | Sonuç |
|---|---|---|
| `SporAkademi.Domain.UnitTests` | `TotpTests`, `UserMfaTests`, `SessionPolicyTests` | 34 geçti, 0 başarısız |
| `SporAkademi.Application.UnitTests` | `IdentityHandlerTests` | 13 geçti, 0 başarısız |
| `SporAkademi.ArchitectureTests` | tamamı (`NotificationsWorkerRulesTests` dahil) | 50 geçti, 0 başarısız |
| `SporAkademi.IntegrationTests` | `MfaTests`, `SessionLifetimeTests`, `RabbitMqDeliveryTests`, `RabbitMqPublisherTests`, `NotificationsWorkerTests` (`ConsumerSelectionTests` dahil), `DocumentStorageContractTests` (S3 ve Azurite alt sınıfları), `AzureBlobAdapterTests`, `ForwardedHeadersHopsTests` | 98 geçti, 0 başarısız (2 dk 50 sn) |

Tam `dotnet test --solution SporAkademi.slnx` bu PR'da yerelde yeniden koşulmadı (yalnız doküman değişti); tam paket her PR'da `backend` işinde ve yukarıdaki `main` koşusunda yeşildir.

## 1. Yöneticiler MFA ile giriyor; yönetici oturumları kısa süreli

**MFA (P5-01, DEC-600 – DEC-609).**

| Durum | Kanıt |
|---|---|
| TOTP RFC 6238'e göre üretilir ve doğrulanır | `TotpTests` (`[Trait("Story","P5-01")]`) |
| Kurulum, onay, kurtarma kodu ve sıfırlama alan kuralları ile komut işleyicileri | `UserMfaTests` (P5-01), `IdentityHandlerTests` (P5-01) |
| MFA'sız Platform Admin kurulum bitmeden token almaz | `MfaTests.A_platform_admin_without_mfa_gets_no_tokens_until_the_enrolment_is_done` |
| Giriş iki adımlı; kod bir kez geçer; challenge 5 dakikada biter; yeni giriş eskisini geçersiz kılar | `MfaTests.Login_with_mfa_is_two_steps_and_the_code_works_once`, `A_challenge_expires_after_five_minutes`, `A_newer_login_replaces_the_earlier_challenge` |
| Kurtarma kodu bir kez çalışır ve sahibine e-posta gider | `MfaTests.A_recovery_code_logs_in_once_and_the_owner_is_told` |
| Yanlış kodlar hesap kilit sayacına yazılır (şifre ve kod hataları tek sayaç) | `MfaTests.Wrong_codes_count_towards_the_account_lockout`, `Wrong_codes_and_wrong_passwords_share_one_failure_count` |
| Okul Admini MFA'yı güncel kodla kapatır; Platform Admin kapatamaz; antrenör MFA kullanamaz | `MfaTests.A_school_admin_can_turn_mfa_off_with_a_current_code_and_a_platform_admin_cannot`, `Coaches_cannot_use_mfa` |
| Platform Admin başkasının MFA'sını sıfırlar: audit'li, oturumlar biter, e-posta gider; kendi MFA'sını sıfırlayamaz; yetki sınırları | `MfaTests.A_platform_admin_resets_a_school_admins_mfa_which_is_audited_ends_the_sessions_and_is_mailed`, `A_platform_admin_resets_another_platform_admin_but_not_themselves`, `Only_a_platform_admin_can_reset_and_a_school_admin_cannot_reach_another_schools_admin` |
| Kuraldan önce açılmış Platform Admin oturumu yenilenemez; MFA açılınca eski oturumlar biter | `MfaTests.A_platform_admin_session_that_predates_the_rule_cannot_be_renewed`, `Turning_mfa_on_ends_the_sessions_that_existed_before` |
| Çözülemeyen sır sabit hata kodudur (500 değil) | `MfaTests.A_secret_that_cannot_be_decrypted_is_a_stable_error_not_a_500_and_not_a_failed_attempt` |
| Zorunluluk yalnız Development'ta kapatılabilir (DEC-608) | `MfaTests.Where_the_rule_is_switched_off_in_development_a_platform_admin_signs_in_with_the_password_alone` |
| Uçtan uca: Platform Admin şifre ve kurulum anahtarından hesaplanan kodla girer | e2e `admin/mfa.e2e.ts` › *a platform admin signs in with a password and a code computed from the setup key* (`e2e` işi) |

Gizli anahtar AES-256-GCM ile şifreli, kurtarma kodları hash'li (DEC-602, DEC-603). `infra/modules/apps` uygulamaya `Mfa__EncryptionKey` sırrını Key Vault'tan verir.

**Oturum süresi (P5-02, DEC-610 – DEC-614).** Platform Admin, Okul Admini ve Antrenör: girişten 12 saat mutlak, son yenilemeden 30 dakika hareketsizlik; veli 7 gün kayan / 30 gün mutlak.

| Durum | Kanıt |
|---|---|
| Politika değerleri | `SessionPolicyTests` (P5-02) |
| 30 dk hareketsiz panel oturumu biter (her yönetici rolü ve antrenör için), 30 dakikadan kısa aralıklarda yaşar | `SessionLifetimeTests.A_panel_session_that_stays_idle_for_thirty_minutes_ends`, `A_panel_session_survives_gaps_just_under_thirty_minutes_and_the_cookie_lives_that_long` |
| 12 saat sonra, ne kadar etkin olursa olsun biter | `SessionLifetimeTests.A_panel_session_ends_twelve_hours_after_login_however_active_it_is` |
| Erişim token'ı oturumdan uzun yaşamaz; süre dolması başka cihazları düşürmez | `SessionLifetimeTests.The_access_token_never_outlives_the_session`, `An_expired_panel_session_is_not_a_reuse_and_other_devices_stay_signed_in` |
| Veli süresi değişmedi | `SessionLifetimeTests.A_parent_session_is_unchanged_seven_days_sliding_and_thirty_days_absolute` |
| Eski oturumlar sonraki yenilemede yeni kurala bağlanır | `SessionLifetimeTests.A_session_created_before_the_change_follows_the_new_idle_rule_at_its_next_refresh`, `A_session_created_before_the_change_follows_the_twelve_hour_limit_at_its_next_refresh` |
| Yanıtı kaybolan yenilemenin tekrarı çalışır | `SessionLifetimeTests.A_lost_response_retry_inside_the_grace_window_still_works_for_a_panel_session` |

ASVS 3.3.2 sapması yönetici rolleri için kapandı. Hareketsizlik "son yenilemeden bu yana" ölçülür, kesin ölçüm değildir (fiilen 15–30 dk, DEC-611).

## 2. Entegrasyon event'leri RabbitMQ üzerinden, kuyruk yokken de doğru; bildirim servisi ayrı süreçte

**RabbitMQ teslimi (P5-03, DEC-620 – DEC-629, [ADR-0016](adr/0016-entegrasyon-event-leri-rabbitmq.md)).** Testler gerçek RabbitMQ (Testcontainers) ile.

| Durum | Kanıt |
|---|---|
| Uçtan uca teslim, iz iki tarafı kapsar | `RabbitMqDeliveryTests.A_published_message_is_consumed_end_to_end_and_the_trace_spans_both_sides` |
| İki kez gelen mesaj bir kez işlenir (inbox) | `RabbitMqDeliveryTests.A_message_delivered_twice_is_handled_once` |
| Bir kez düşen mesaj yeniden denenip geçer; zehirli mesaj en fazla deneme sonrası dead-letter'a gider | `A_message_that_fails_once_is_retried_and_then_succeeds`, `A_poison_message_is_retried_then_dead_lettered_after_the_maximum_attempts` |
| Okunamayan yük ve çözülemeyen okul başlığı yeniden denenmeden dead-letter'a gider (platform bağlamında çalışmaz) | `A_message_with_an_unreadable_payload_is_dead_lettered_without_retries`, `A_message_with_an_unparsable_tenant_header_is_dead_lettered_not_run_in_platform_scope` |
| Tüketici mesajın okulunda çalışır; tüketicisi olmayan tür işlenmiş sayılmaz; kapatılamayan teslimi broker dead-letter'lar | `The_consumer_runs_in_the_school_of_the_message`, `A_type_without_a_consumer_is_not_marked_processed`, `A_delivery_that_cannot_be_settled_is_dead_lettered_by_the_broker_instead_of_looping` |
| **Broker kesintisinde** mesaj outbox'ta bekler, kurtarmadan sonra teslim edilir; hazırlık `Degraded` | `A_broker_outage_leaves_the_message_in_the_outbox_and_it_is_delivered_after_recovery`, `Readiness_is_degraded_while_the_broker_is_unreachable` |
| Yayımcı: okul mesajı `tenant-id` başlığı taşır, platform mesajı taşımaz | `RabbitMqPublisherTests.A_message_of_a_school_always_carries_its_tenant_header_and_a_platform_message_none` |

**Kuyruk yokken.** `ConnectionStrings:RabbitMq` yoksa süreç içi teslim bugünkü gibi çalışır (DEC-620, DEC-628). Bu yolun testleri (`OutboxTests`, `OutboxWorkerTests`, `OutboxPolicyTests`) broker kurmadan koşar ve her PR'da `backend` işinde çalışır. Bildirim worker'ı olmadan API e-postayı kuyrukta bekletir, worker gelince gönderir: `NotificationsWorkerTests.Without_the_worker_the_api_leaves_the_notification_waiting_in_its_queue_and_the_worker_sends_it_later`.

**Bildirim worker'ı (P5-04, DEC-630 – DEC-639, [ADR-0017](adr/0017-bildirim-worker.md)).**

| Durum | Kanıt |
|---|---|
| E-posta worker'dan bir kez gider, diğer mesajlar API'de kalır | `NotificationsWorkerTests.A_notification_is_sent_once_by_the_worker_while_other_messages_stay_with_the_api` |
| Worker sağlık probe'larına yanıt verir, tüketiciler çalışınca hazırdır | `NotificationsWorkerTests.The_worker_answers_its_health_probes_and_is_ready_once_its_consumers_run` |
| Dağıtım ayarı worker'ın tüketicilerini genişletemez | `NotificationsWorkerTests.A_deployments_settings_cannot_widen_the_workers_consumers` |
| Worker production'da MFA ya da imza anahtarı olmadan başlar | `NotificationsWorkerTests.The_worker_starts_in_production_without_any_mfa_or_signing_key` |
| Tüketici seçimi (varsayılan hepsi, grup dışlama, grup seçme, modül süzgeci) | `ConsumerSelectionTests` (4 test, P5-04) |
| Tam olarak e-posta handler'ları `notifications` grubunda; worker yalnız Application, Infrastructure ve Domain'a bağlı; hiçbir katman worker'a bağlı değil; API ve worker derleme çıktıları birbirini içermez | `NotificationsWorkerRulesTests` (mimari, P5-04) |
| Kendi imajı CI'da derlenir ve duman testinden geçer, main'de GHCR'ye yayımlanır | `images` işi, `publish` işi |

**Yerel uçtan uca (Mailpit) denemesi yapılmadı.** compose `messaging` profili + `scripts/run-api.sh` + `scripts/run-notifications.sh` ile bir e-postanın Mailpit'e düşmesi, tohumlanmış veritabanı ve bir e-posta tetikleyicisi gerektirir; bu doğrulamada ucuz değildi, atlandı. Aynı yol (gerçek PostgreSQL, gerçek RabbitMQ, ayrı worker hostu) yukarıdaki `NotificationsWorkerTests` ile otomatik doğrulanıyor.

## 3. Altyapı kod olarak tanımlı ve CI'da doğrulanıyor; taşıma runbook'u ve maliyet tahmini hazır

**Kod ve CI (P5-05, DEC-640 – DEC-649, [ADR-0015](adr/0015-altyapi-kodu-araci.md)).** `infra/` altında OpenTofu (Terraform dili): `infra/modules/{foundation,data,apps,stack}`, `infra/envs/{staging,production}`; Container Apps (API, admin, veli, gateway, migration job, isteğe bağlı RabbitMQ ve bildirim worker'ı), PostgreSQL Flexible Server, Key Vault, Log Analytics, belge depolama. Açıklama: [bulut-altyapisi.md](bulut-altyapisi.md).

CI doğrulaması: `repo-checks` işi, adım **Infrastructure as code (OpenTofu fmt, validate; Trivy config; pinned binaries + checksums, P5-05, DEC-642)** (`.github/workflows/ci.yml`). Sabit sürümlü OpenTofu ve Trivy ikilileri sağlama toplamı doğrulanarak indirilir; sonra `tofu fmt -check -recursive infra`, her ortam için `tofu init -backend=false -lockfile=readonly` + `tofu validate`, ve `trivy config --severity HIGH,CRITICAL --exit-code 1 infra`. Bu adım son tamamlanmış `main` koşusunda (`1c5a569`) başarılıdır. Kabul edilen bulgular satır içi `#trivy:ignore` yorumlarıyla gerekçelidir. `plan` ve `apply` CI'da yoktur.

**Bulut çalıştırmaya hazırlık (P5-06, DEC-650 – DEC-657).**

| Durum | Kanıt |
|---|---|
| Belge deposu sözleşmesi S3 ve Azure Blob (Azurite) adaptörlerinde aynı testlerle | `DocumentStorageContractTests` (9 test, P5-06) |
| Azure Blob adaptörü: kullanıcı delegasyon anahtarı bir kez alınır ve süresi bitmeden yenilenir; hazırlık; sağlayıcı seçimi (S3 varsayılan, Azure'da anahtarsız yönetilen kimlik, https zorunlu); eksik ayarda başlangıç davranışı | `AzureBlobAdapterTests` (11 test, P5-06) |
| `ForwardLimit` ile vekil zinciri: varsayılan 1 atlamada iki vekil herkesi iç vekil gösterir; 2'de her istemcinin kendi bütçesi vardır; başlığa adres eklemek yeni bütçe vermez; güvenilmeyen atlama hiç güvenilmez | `ForwardedHeadersHopsTests` (6 test, P5-06) |
| Migrator `ConnectionStrings__Migrator` ile çalışır | `DesignTimeConnectionTests` (3 test, P5-06), `images` işi *Smoke test migrator and API against a fresh database* |
| Hazırlık yalnız veritabanı için 503; isteğe bağlı bağımlılıklar (belge deposu) `Degraded` | `HealthTests` (P5-06 işaretli test dahil) |

**Taşıma runbook'u:** [bulut-tasima-runbook.md](bulut-tasima-runbook.md) (hazırlık, iki aşamalı `apply`, roller ve sırlar, veri taşıma, DNS ve kesim, rollback; her adım **[kanıtlandı]** ya da **[denenmedi]** işaretli).
**Maliyet tahmini:** [bulut-altyapisi.md](bulut-altyapisi.md) bölüm 5 (2026-11-17 perakende fiyatları; staging ~27, production ~62, birlikte ~89 USD/ay; RabbitMQ ve Redis kapalı).

## Kanıtlanmayanlar ve sahibe kalanlar

Faz 5 kodu ve altyapı kodu tamam; aşağıdakiler **kanıtlanmadı**:

- **Azure'da hiçbir şey uygulanmadı.** `tofu plan` ve `apply` hiç çalıştırılmadı (bulut hesabı yok). Doğrulama `fmt`, `validate` ve Trivy ile sınırlı; sağlayıcı şeması, kota, bölge ve kaynak sağlayıcısı kayıtları ilk staging `plan`/`apply`'da görülecek. Runbook'un **[denenmedi]** adımları Azure'da hiç koşmadı.
- **Kullanıcı delegasyonlu SAS gerçek Azure'a karşı denenmedi.** Azurite bu imza türünü desteklemez; testte yalnız paylaşılan anahtarlı imzalayıcı kullanıldı (DEC-650). Gerçek doğrulama ilk staging'dir.
- **`ForwardLimit=2` staging'de doğrulanmalı.** Azure giriş zincirine dair varsayım (DEC-651); testler yalnız kod davranışını kanıtlar. Gerçek veriden önce şart (runbook bölüm 8).
- **PostgreSQL 18 ve `BYPASSRLS`'li yedek rolü Flexible Server'da denenmedi.** Sürüm 18'in seçilen bölgede sunulması ve yönetici rolün `BYPASSRLS` verip verememesi açık risk (DEC-571, DEC-656; bulut-altyapisi.md bölüm 8). Migrator rolüyle `pg_restore` yerelde denendi, Flexible Server'da denenmedi.
- **Maliyet bütçeyi aşıyor:** ~89 USD/ay tahmini, NFR-CST-01 hedefi 20 USD/ay'ın yaklaşık 4,5 katı (DEC-648). Karar ve seçenekler sahipte.
- **[ADR-0014](adr/0014-veri-konumu.md) bölge kararı** Proposed; Azure'da Türkiye bölgesi yok, bölge değişkendir, coğrafi yedek kapalı (DEC-649). Hukuki teyit sahipte.
- **MFA anahtar döndürme yok** (DEC-602): `Mfa:EncryptionKey` verilmezse JWT anahtarından türetilir ve JWT anahtarını döndürmek tüm MFA sırlarını geçersiz kılar; production'a ayrı anahtar verilmeli. Kurtarma kodu yenileme ve antrenör MFA'sı da yok.
- **`npm audit` `picomatch` istisnası 2026-12-17'de bitiyor** (DEC-670, DEC-671; `.github/npm-audit-allowlist.json`). Yamalı sürüm çıkarsa yükseltilip kayıt silinir; çıkmazsa sahip süreyi gerekçeyle yeniler. Süre geçerse `frontend` ve `parent` kırmızı olur.
- **Yerel Mailpit uçtan uca denemesi yapılmadı** (yukarıda gerekçesiyle); bu yol yalnız otomatik testlerle kanıtlı.
- Ayrıca: hareketsizlik sınırı kesin değil (DEC-611), RabbitMQ'nun production'daki yeri ve worker'ın açılması (DEC-645), SMTP sağlayıcısı seçimi, Deploy bloğu, P5-R2 Faz 5 geçişi ve `v1.0.0` etiketi. Faz 5 kararlarının tamamı Proposed; [onay listesi](pazartesi-onay-listesi.md) bölüm 12, teknik borç sırası [v1-mimari-inceleme.md](v1-mimari-inceleme.md) bölüm 5.
