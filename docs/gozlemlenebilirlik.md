# Gözlemlenebilirlik (P4-07, NFR-OBS-04)

API trace ve metrikleri OpenTelemetry ile OTLP üzerinden, beklenmeyen hataları Sentry'ye gönderir. Loglar değişmedi: JSON stdout ve yerelde Seq (NFR-OBS-01). Kararlar: DEC-550 – DEC-557.

## Yerelde çalıştırma

```bash
bash scripts/setup-env.sh                                   # eksik port anahtarlarını .env'e ekler
docker compose --profile observability up -d --wait         # Jaeger + Prometheus + Perses
bash scripts/run-api.sh                                     # profil çalışıyorsa API'yi ona bağlar
```

| Araç | Adres (varsayılan port) | Ne için |
| --- | --- | --- |
| Jaeger UI | http://127.0.0.1:56686 | Trace'ler (servis `spor-akademi-api`) |
| Perses | http://127.0.0.1:58080 | Dashboard: Spor Akademi → Spor Akademi API |
| Prometheus | http://127.0.0.1:57090/alerts | Alarm kuralları ve durumları |
| OTLP alıcısı | 127.0.0.1:54317 (gRPC), 54318 (HTTP) | API'nin gönderdiği adres |

Hepsi yalnız 127.0.0.1'e bağlanır ve hiçbir şey kalıcı değildir; trace'ler Jaeger yeniden başlayınca silinir. İmajların hepsi Apache-2.0 lisanslıdır (DEC-554). Perses'te kimlik doğrulama yoktur; arayüzde yapılan değişiklikler kaybolur, dashboard'u `docker/observability/perses/provisioning/` altındaki dosyada değiştirin.

## Yapılandırma

| Anahtar | Varsayılan | Açıklama |
| --- | --- | --- |
| `OpenTelemetry:Otlp:Endpoint` (yoksa `OTEL_EXPORTER_OTLP_ENDPOINT`) | yok | Yoksa hiçbir şey kaydedilmez ve gönderilmez; uygulama aynen çalışır. |
| `OpenTelemetry:Otlp:Protocol` (yoksa `OTEL_EXPORTER_OTLP_PROTOCOL`) | `grpc` | `grpc` veya `http/protobuf`. HTTP'de `/v1/traces` ve `/v1/metrics` eklenir. Kimlik başlıkları standart `OTEL_EXPORTER_OTLP_HEADERS` ile verilir. |
| `OpenTelemetry:Tracing:SampleRatio` | `1.0` | Kök span'lerin örneklenme oranı. |
| `OpenTelemetry:Metrics:ExportInterval` / `HealthInterval` | 15 sn / 30 sn | Metrik gönderimi ve sağlık ölçümü aralığı. |
| `OpenTelemetry:Enabled` | `false` | Endpoint olmadan kayıt (testlerde in-memory exporter için). |
| `Sentry:Dsn` (ortam değişkeni `Sentry__Dsn`) | yok | Yoksa Sentry kapalıdır ve hiçbir şey göndermez. Gerçek DSN Deploy bloğunda. |

## Sinyaller

**Trace:** gelen HTTP istekleri (`/health/live` hariç), giden HttpClient çağrıları (ödeme sağlayıcısı, depo), Npgsql sorguları (`db.query.text` parametreli SQL'dir, parametre değeri yazılmaz), her outbox teslimi (`outbox.deliver`), her Hangfire işi (`job.run`; 10 saniyede bir çalışan outbox işi hariç, onun teslimleri zaten span), SignalR yayını (`realtime.notify`). İstek span'inde `spor_akademi.correlation_id` vardır. Loglar span'in `TraceId`'sini taşır (`@tr`), böylece problem yanıtındaki `traceId` (korelasyon ID'si) → loglar → trace zinciri kurulur.

**Metrikler** (Prometheus adlarıyla; hiçbirinde tenant veya kullanıcı etiketi yok):

| Metrik | Etiketler | Kaynak |
| --- | --- | --- |
| `http_server_request_duration_seconds` | route, method, status | ASP.NET Core |
| `aspnetcore_diagnostics_exceptions_total` | error_type | ASP.NET Core |
| `aspnetcore_rate_limiting_requests_total` | result | ASP.NET Core |
| `spor_akademi_health_status` | check (`overall` dahil): 2 sağlıklı, 1 Degraded, 0 Unhealthy | `/health/ready` kontrolleri, 30 sn'de bir |
| `spor_akademi_outbox_pending`, `_dead_lettered`, `_overdue_seconds` | — | outbox sağlık kontrolü |
| `spor_akademi_outbox_messages_total` | result (delivered, retried, dead_lettered), message_type | outbox işlemcisi |
| `spor_akademi_auth_login_failures_total` | reason (hata kodu) | giriş uç noktası |
| `spor_akademi_payments_online_notifications_total` | outcome, status | webhook ve tarayıcı dönüşü |
| `spor_akademi_realtime_notices_total` | audience (users, role) | SignalR |

Ayrıca HttpClient, SignalR bağlantıları ve .NET runtime (GC, thread pool) metrikleri gider.

**Hata takibi (Sentry):** yalnız beklenmeyen istek hataları (500). Exception türü, stack trace, route, sürüm ve trace ID gider. Exception mesajı, istek gövdesi, cookie, header, query string, kullanıcı ve breadcrumb gitmez (DEC-556). Arka plan işlerinin hataları Sentry'ye gitmez; onları outbox dead-letter alarmı ve Hangfire paneli (`/jobs`) gösterir.

## Kişisel veri

Hiçbir sinyal kişisel veri veya token taşımaz (NFR-PRV-03, DEC-551). Her span dışarı çıkmadan önce `TelemetryScrubber`'dan geçer. Query string'ler (`access_token`, SignalR bağlantı kimliği, ödeme dönüş token'ı, arama terimi) silinir. Header, cookie, istemci IP'si, user agent ve SQL parametreleri yazılmaz. Exception mesajı span'e eklenmez. Çağıranın baggage'ı okunmaz ve dışarı aktarılmaz; uzak çağıranın "sampled" bayrağına da güvenilmez (DEC-553). Kanıt: `IntegrationTests/Observability/TelemetryTests` ve `ErrorTrackingTests` (in-memory exporter, sahte Sentry transport'u). Kalan risk: Npgsql bir veritabanı hatasında span'e PostgreSQL'in hata metnini olay olarak ekleyebilir. Bu metinde değerin kendisi (`Detail`) yoktur, çünkü `Include Error Detail` kapalıdır.

## Alarmlar

Kurallar `docker/observability/alerts.yml`'dedir, eşiklerin gerekçesi DEC-555'tedir. Yerelde kimseye bildirim gitmez. Production'da bildirim kanalı (e-posta, Alertmanager veya yönetilen servis) Deploy bloğunda kurulur.

| Alarm | Koşul | Önem |
| --- | --- | --- |
| `ApiHighErrorRate` | 5xx oranı > %2, 5 dk (trafik > 3 istek/dk) | critical |
| `ApiSlowP95` | p95 > 500 ms, 10 dk (hub ve `/jobs` hariç) | warning |
| `ApiNotReady` | `/health/ready` Degraded/Unhealthy, 5 dk | warning |
| `ApiDatabaseUnhealthy` | veritabanı kontrolü Unhealthy, 2 dk | critical |
| `ApiMetricsMissing` | sağlık metriği 10 dk gelmiyor | warning |
| `OutboxDeadLetters` | dead-letter > 0, 1 dk | warning |
| `OutboxBacklog` | vadesi gelmiş mesaj > 5 dk bekliyor | warning |
| `LoginFailureSpike` | başarısız giriş > 30/dk, 5 dk | warning |
| `RateLimitRejections` | rate limit reddi > 60/dk, 10 dk | warning |
| `PaymentProviderUnavailable` | 15 dk'da ≥ 3 bildirim sağlayıcıya sorulamadı | warning |

**Dead-letter olunca:** `outbox.messages`'ta `dead_lettered_at` dolu satırın `type` ve `last_error`'ına bakın (yük kişisel veri içerebilir, dışarı kopyalamayın). Nedeni giderin, sonra satırı yeniden kuyruğa alın (`dead_lettered_at = NULL, attempts = 0, next_attempt_at = now()`). Bunun için bir yönetim aracı henüz yok.
