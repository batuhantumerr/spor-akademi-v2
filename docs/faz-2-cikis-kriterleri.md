# Faz 2 çıkış kriterleri: doğrulama

Faz 2 (`v0.2.0`, P2-F1 – P2-F8) çıkış kriterlerinin ([proje dokümanı §5](proje-dokumani.md)) hangi otomatik testlerle kanıtlandığı (P2-R1, #77). Hepsi her PR'da zorunlu check'lerde çalışır: entegrasyon testleri `backend` işinde (Testcontainers, gerçek PostgreSQL), uçtan uca testler `e2e` işinde (Playwright; masaüstü Chromium, Firefox ve WebKit). Faz geçişi (P2-R2) ve `v0.2.0` etiketi sahiptedir.

## 1. Bir veli sandbox sağlayıcısı üzerinden bir aidatı uçtan uca ödeyebilir

| Katman | Test |
|---|---|
| e2e | `web/admin/e2e/admin/school-admin.e2e.ts` › *a due is paid online through the sandbox provider and recorded once*: Okul Admini ödenmemiş borçlar listesinden online ödeme başlatır; ödeyen, paylaşılan bağlantıyı oturumsuz ayrı bir tarayıcıda açar, Fake sağlayıcının sandbox sayfasında öder ve sonuç sayfasına düşer. Ödeme listesinde tek satır (yöntem Online), online ödeme "Ödendi", borç listesi boş; API'den okunan ödemeler tam olarak bir `Online`/`Recorded` kayıt. |
| Entegrasyon | `OnlinePaymentTests.A_due_is_paid_end_to_end_through_the_sandbox_provider` (başlat → sandbox → tarayıcı dönüşü → 303 → ödeme kaydı → borç kapanır) |

Veli portalı Faz 3'te geldiği için "veli", Okul Admininin paylaştığı bağlantıyı açan oturumsuz kişidir (DEC-348). e2e API'si bu yüzden `Staging` + Fake sağlayıcıyla çalışır (DEC-241). Gerçek iyzico sandbox'ına karşı deneme anahtar gerektirdiği için açık kalır (roadmap P2-F1).

## 2. Başarısız bir webhook çift tahsilat olmadan yeniden denenir

`tests/SporAkademi.IntegrationTests/Billing/`:

| Durum | Test |
|---|---|
| Sağlayıcı ulaşılamaz → `503`, sağlayıcının yeniden denemesi tek kayıt, ardından tekrar `Unchanged` | `OnlinePaymentWebhookTests.A_webhook_that_fails_is_retried_by_the_provider_and_credited_once` |
| Ödeme yazılırken veritabanı hatası → `500`, hiçbir şey kalmaz (bildirim, ödeme yok, deneme `Pending`); yeniden deneme tek kayıt, tekrar `Unchanged` | `OnlinePaymentWebhookTests.A_webhook_that_fails_while_writing_the_payment_is_rolled_back_and_its_retry_credits_once` (bu PR'da eklendi) |
| Aynı webhook ikinci kez | `OnlinePaymentWebhookTests.A_signed_webhook_records_the_payment_and_a_duplicate_is_acknowledged_without_a_second_credit` |
| Eşzamanlı webhook'lar ve tarayıcı dönüşleri | `OnlinePaymentWebhookTests.Concurrent_webhooks_and_browser_returns_credit_the_due_exactly_once` |
| Sağlayıcı henüz "beklemede" derken gelen webhook tüketilmez, sonraki teslimde uygulanır | `OnlinePaymentWebhookTests.A_webhook_received_while_the_provider_still_reports_pending_is_applied_when_delivered_again` |
| Webhook hiç gelmez: mutabakat işi bir kez kaydeder | `OnlinePaymentReconcileJobTests.A_paid_attempt_whose_webhook_was_lost_is_credited_once` |
| İmzasız/sahte webhook reddedilir, hiçbir şey değişmez | `OnlinePaymentWebhookTests.A_webhook_with_a_wrong_or_missing_signature_changes_nothing` |
| Veritabanı başarılı ödemeyi ve kaydını korur | `OnlinePaymentWebhookTests.The_database_keeps_a_succeeded_online_payment_and_its_credit_as_they_are` |

## 3. Anonimleştirme zamanında çalışır ve her çalışma loglanır

| Durum | Test |
|---|---|
| İş her gün 05:00'te (İstanbul; `0 2 * * *` UTC) zamanlanmış | `JobServerTests.The_anonymization_job_is_scheduled_daily_at_05_00_in_Istanbul` (bu PR'da eklendi); kaydın kendisi `JobServerTests.The_server_registers_the_recurring_jobs_and_reports_ready` |
| Süresi dolan anonimleşir, dolmayan ve aktif olan dokunulmaz | `AnonymizationTests.A_student_who_left_on_the_cutoff_is_anonymized_one_who_left_a_day_later_waits_and_an_active_one_is_never_touched` |
| Her çalışma `students.anonymization_runs`'a sayılarla (kişi adı olmadan) yazılır, tekrar çalışma bir şey değiştirmez | `AnonymizationTests.Running_again_changes_nothing_and_every_run_is_recorded_with_counts_only` |
| Başarısız çalışma `Failed` olarak kaydedilir ve iş başarısız olur | `AnonymizationTests.A_failing_run_is_recorded_as_failed_and_the_job_fails` |
| Kesme tarihi okulun saat diliminde | `AnonymizationTests.The_cutoff_uses_today_in_the_school_time_zone_not_the_utc_date` |
| Her okul kendi politikası, kendi çalışmaları | `AnonymizationTests.Each_school_applies_its_own_policy_and_sees_only_its_own_runs` |

Zamanlamanın Hangfire tarafından tetiklenmesi Hangfire'ın sorumluluğudur; test kayıtlı cron'u, saat dilimini ve bir sonraki çalışmanın İstanbul saatini doğrular.

## Ek: P2-F8 aidat kuralları

`school-admin.e2e.ts` › *a sibling discount defined under fee rules shows on the dues list*: Ayarlar → Aidat kuralları'ndan bu ay için kural tanımlanır (onay penceresiyle); aynı veliye bağlı iki öğrenciden ikincisinin borç satırında liste fiyatı üstü çizili ve "Kardeş indirimi %10" ipucu görünür. Hesaplamanın kendisi `FeeRulesTests`'te.
