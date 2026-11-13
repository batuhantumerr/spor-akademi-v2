# Faz 4 çıkış kriterleri: doğrulama

Faz 4 (`v0.4.0`, P4-01 – P4-11) çıkış kriterlerinin ([roadmap](roadmap.md), P4-R1) neyle kanıtlandığı. Entegrasyon testleri `backend` işinde (Testcontainers, gerçek PostgreSQL), uçtan uca testler `e2e` işinde, erişilebilirlik taraması `a11y` işinde, imaj ve başlık kontrolleri `images` işinde her PR'da çalışır. Otomatik olmayan kanıtlar (yerel ölçümler, tatbikat) ayrıca işaretlenmiştir. Faz geçişi (P4-R2) ve `v0.4.0` etiketi sahiptedir; Deploy bloğu kapsam dışıdır.

## 1. OWASP incelemesinde açık yüksek bulgu yok; güvenlik başlıkları otomatik kontrol ediliyor

| Kanıt | Nerede |
|---|---|
| OWASP Top 10 (2021) A01–A10 ve ASVS 4.0.3 L2 eşlemesi; yüksek bulgu yok, orta bulgulardan üçü düzeltildi, üçü gerekçeyle ertelendi (DEC-510 – DEC-518) | [guvenlik-kontrol-listesi.md](guvenlik-kontrol-listesi.md) bölüm 4 |
| API başlıkları (CSP, nosniff, Referrer-Policy, frame engeli), hata yanıtları dahil; HSTS yalnız Production + HTTPS (proxy arkasında bilinen proxy'den) | `SecurityHeadersTests` (`Api_responses_carry_the_security_headers`, `Signed_in_and_error_responses_carry_them_too`, `Production_sends_hsts_over_https_only`, `Behind_a_known_proxy_production_sends_hsts_when_the_client_used_https`) |
| Admin ve veli imajlarında tam CSP, HSTS, Permissions-Policy | `images` işi: `scripts/check-security-headers.sh api|admin|parent` |
| Tarayıcıda CSP ihlali olmaz (her e2e testi ihlali yakalar ve testi kırar) | `web/admin/e2e/support/test.ts` (`securitypolicyviolation`), `admin/security-headers.e2e.ts` |
| Rate limiting: kullanıcı, okul yazma ve IP bazlı sınırlar, `429` + `Retry-After`; Production'da limitler yükseltilemez | `ApiRateLimitingTests` (DEC-503 – DEC-507, DEC-561, DEC-569) |
| Her oturumlu uç nokta bir rol adlandırır | `EndpointSecurityTests` (mimari testi, DEC-511) |

## 2. RLS, uygulama filtresi kapalıyken bile okullar arası okuma ve yazmayı engelliyor

`tests/SporAkademi.IntegrationTests/Tenancy/RowLevelSecurityTests.cs` (DEC-520 – DEC-528):

| Durum | Test |
|---|---|
| Filtresiz (`IgnoreQueryFilters`, ham SQL) okumada başka okulun satırı görünmez | `Without_query_filters_another_schools_rows_cannot_be_read` |
| Başka okula satır eklenemez, değiştirilemez, silinemez, taşınamaz | `Another_schools_row_cannot_be_inserted`, `Another_schools_rows_cannot_be_changed_or_deleted`, `A_row_cannot_be_moved_to_another_school` |
| Okul bağlamı olmayan bağlantı hiçbir şey görmez; havuzdaki bağlantı önceki okulu taşımaz | `A_connection_without_the_session_settings_sees_nothing`, `A_pooled_connection_does_not_keep_the_previous_school` |
| Uygulama rolü RLS'i aşamaz; bakım anahtarı yalnız migrator'da çalışır | `The_application_role_cannot_bypass_row_level_security`, `The_maintenance_switch_only_works_for_the_migrator` |
| Okul verisi tutan her tablo `FORCE` RLS ve okulu okuyan bir politika taşır (yeni tablo unutulursa test kırılır) | `Every_table_forces_row_level_security_with_a_policy_that_reads_the_school` |
| Okul bayrakları da RLS altında; okul kendi değerini yazamaz | `FeatureFlagTests.Row_level_security_shows_a_school_only_its_own_values`, `A_school_scope_can_read_but_never_write_its_own_values` |

Faz 1'deki uygulama katmanı izolasyon paketi (P1-35) aynen geçmeye devam ediyor.

## 3. Kişisel veri erişimi audit log'da; ilgili kişinin verisi dışa aktarılıp silinebiliyor

| Durum | Test |
|---|---|
| Değişiklikler kim, ne zaman, hangi kayıt ve hangi alan adlarıyla yazılır; değer yazılmaz | `AuditLogTests.Changes_record_the_actor_the_record_and_the_changed_field_names_but_no_values` |
| Ayrıntı okumaları, belge açmaları ve liste sorguları kaydedilir (antrenörün fotoğraf açması dahil) | `AuditLogTests.Detail_reads_document_opens_and_list_queries_are_recorded_once_each`, `A_coach_opening_a_photo_is_recorded_with_the_coach_role` |
| Uygulama rolü kayıt ekler ve okur, değiştiremez ve silemez; başka okulun kayıtları görünmez; saklama süresi sahip fonksiyonuyla | `The_application_role_can_add_and_read_entries_but_never_change_or_delete_them`, `Another_schools_entries_are_never_visible`, `The_retention_job_deletes_entries_older_than_two_years_through_the_owners_function` |
| Dışa aktarma öğrenci, veli, kayıt, yoklama, ödeme (serbest metinler dahil) ve belge bağlantılarını içerir; yalnız o okulun Okul Admini | `DataSubjectRequestTests.The_export_holds_the_student_guardians_enrollments_attendance_payments_and_document_links`, `The_export_holds_free_text_of_payments_refunds_and_online_payment_attempts`, `Only_the_school_admin_of_the_students_school_exports` |
| Silme anonimleştirmeyle aynı sonucu verir; açık online ödeme varken bekler | `Erasure_ends_an_active_student_today_and_leaves_exactly_what_anonymization_leaves`, `Erasure_waits_while_an_online_payment_is_still_open_at_the_provider` |
| Uçtan uca: Okul Admini öğrencinin verisini indirir, siler ve audit listesinde ikisini görür | e2e `admin/school-admin.e2e.ts` › *exports and erases a student on request; the audit log shows both (P4-05, P4-06)* |

Aydınlatma metni taslağı [aydinlatma-metni.md](aydinlatma-metni.md) (iki uygulamadaki kopyalar `repo-checks`'te `scripts/privacy-notice.mjs --check` ile eşit tutulur) ve veri konumu [ADR-0014](adr/0014-veri-konumu.md) Proposed; hukuki teyit ve karar sahipte.

## 4. Trace ve metrikler dashboard'da görünüyor

| Kanıt | Nerede |
|---|---|
| Span'ler kişisel veri, query string, header ve SQL değeri taşımaz; log olayları trace ID taşır; metriklerde okul/kişi etiketi yok | `TelemetryTests` (`Spans_carry_no_personal_data_query_strings_headers_or_sql_values`, `Log_events_carry_the_trace_id_of_the_request_span`, `Metrics_count_failed_logins_by_code_without_tenant_or_personal_attributes`), `ErrorTrackingTests` (Sentry DSN yokken hiçbir şey gönderilmez) |
| Yerel yığın: compose `observability` profili (Jaeger, Prometheus, Perses); dashboard'lar ve 12 alarm kuralı repoda | [gozlemlenebilirlik.md](gozlemlenebilirlik.md), `docker/observability/` |
| Dashboard'da görünme | **Elle doğrulandı** (P4-07, PR #136): API'ler OTLP ile Jaeger'a trace gönderdi, metrikler Prometheus'a ulaştı, Perses dashboard'u veriyle çizildi; alarm kuralları `promtool check rules` ile geçti. CI'da otomatik dashboard testi yok. |

## 5. Yük testi bütçe içinde ve iki API örneğiyle çalışıyor

**Yerel ölçüm** (P4-08, PR #146; ayrıntı [performans.md](performans.md)): 300 eşzamanlı sanal kullanıcı, 14 okul / 5 845 öğrenci, Release imajı (`LoadTest` ortamı).

| Durum | Okuma p95 (bütçe 300 ms) | Yazma p95 (bütçe 500 ms) | Hata |
|---|---|---|---|
| Tek API örneği | 12,9 ms | 72,5 ms | %0 |
| Caddy arkasında iki örnek | 13,9 ms | 77,8 ms | %0 |

İki örnekte oturum ve refresh her iki örnekte çalışır, SignalR olayları Valkey backplane ile taşınır; hub bağlantıları proxy'de örnek bağlılığı (cookie affinity) ister (Deploy bloğuna not). Regresyon kontrolü: `QueryCountTests.The_query_count_does_not_grow_with_the_school` (18 uç nokta, `backend` işinde). Elle tetiklenen `load-smoke` iş akışı zorunlu değildir.

## 6. Geri yükleme tatbikatı süresiyle belgeli

**Yerel tatbikat** (P4-09, PR #137; runbook [geri-yukleme-tatbikati.md](geri-yukleme-tatbikati.md)): mantıksal tam yedekten ve WAL arşiviyle belirli bir ana (PITR) geri yükleme, RLS'li şemada; iki geri yükleme de kaynakla birebir eşleşti (satır sayıları ve md5, migration'lar, yetkiler, RLS politikaları). Felaketten doğrulanmış veritabanına ~9–20 sn (sentetik veri, tek makine; alt sınır). `restore-drill` iş akışı haftalık ve ilgili PR'larda çalışır (zorunlu değil). 15 dk RPO sunucu dışı WAL kopyasına bağlıdır ve Deploy bloğunda tamamlanır.

## 7. Bir özellik okul bazında bayrakla açılıp kapanıyor

`tests/SporAkademi.IntegrationTests/Tenancy/FeatureFlagTests.cs` (DEC-580 – DEC-585):

| Durum | Test |
|---|---|
| Platform Admin okulun bayrağını değiştirir; değer yalnız o okulda geçerli | `Setting_a_flag_stores_the_schools_own_value`, `A_disabled_feature_answers_like_an_unknown_route_for_that_school_only` |
| Kapalı özellik bilinmeyen yolla aynı `404`'ü verir; yetki kontrolü önce gelir | `A_disabled_feature_answers_like_an_unknown_route_for_that_school_only`, `Authorization_still_comes_first_on_a_disabled_feature` |
| Değişiklik okulun önbelleğini hemen geçersiz kılar | `A_change_invalidates_the_schools_cached_flags_at_once` |
| Veli online ödemesi bayrak kapalıyken gizlenir (durum sorgusu açık kalır) | `Parent_online_payment_is_hidden_while_its_flag_is_off` |

Admin ve veli arayüzündeki davranış birim testlerle (Angular `FeatureService`/guard, React `useFeature`) kanıtlı; paylaşılan e2e okulunda bayrak kapatmak paralel testleri bozacağı için e2e eklenmedi (DEC-585).

## Sahibe kalanlar

- P4-R2 Faz 4 geçişi ve `v0.4.0` etiketi.
- Faz 4 kararları (DEC-500 – DEC-594) Proposed; [onay listesi](pazartesi-onay-listesi.md) bölüm 11.
- ASVS L2 sapmaları (oturum süresi, MFA, antivirüs/SBOM/tehdit modeli), aydınlatma metninin hukuki teyidi, veri konumu (ADR-0014).
- `a11y` ve `parent` job'larının ruleset'e eklenmesi; Deploy bloğu (HSTS/CSP'nin proxy'de doğrulanması, `ForwardedHeaders:KnownProxies`, hub affinity, şifreli ve sunucu dışı yedek, gerçek Sentry DSN, gözlem servisi ve alarm kanalı).
