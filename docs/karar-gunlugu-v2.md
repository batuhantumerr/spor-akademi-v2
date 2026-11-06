# Karar günlüğü — v2 eklemeleri

[Proje dokümanındaki](proje-dokumani.md) DEC-001 – DEC-096 aynen geçerlidir. Bu dosya, bu repoda (spor-akademi-v2) alınan yeni kararları içerir. Sahip yokken agent'ın aldığı kararlar **Proposed** durumundadır; sahip onaylayınca **Accepted** olur.

| ID | Karar | Durum |
| --- | --- | --- |
| DEC-097 | Proje yeni repoda (`spor-akademi-v2`) sıfırdan kuruluyor; eski `spor-akademi` reposuna dokunulmuyor. Sahibin 2026-11-06 kararı. | Accepted |
| DEC-098 | Hafta sonu otonom çalışmada agent, dört zorunlu check yeşil ve bağımsız agent incelemesi yapılmışsa PR'ı kendisi squash merge eder. Sahibin 2026-11-06 kararı. | Accepted |
| DEC-099 | Docker Compose proje adı `spor-akademi-v2` (eski projenin `spor-akademi_postgres-data` volume'u ile çakışmasın diye). Yerel PostgreSQL host portu varsayılanı `55432`. | Proposed |
| DEC-100 | Uygulama rolünün migration geçmiş tablosuna erişimi, `public` şemasında `USAGE` yetkisi kaldırılarak engellenir (init script `REVOKE ALL ON SCHEMA public FROM PUBLIC`). Modül şemalarına `ALTER DEFAULT PRIVILEGES` ile erişim verilir. | Proposed |
| DEC-101 | Trivy `v0.76.1` (doküman sürümü); `v0.76.2` 2026-11-05'te çıktığı için 7 günlük cooldown'a takılıyor. | Proposed |
| DEC-102 | `IDE0005` (gereksiz using) build'de hata olarak zorlanmıyor: `GenerateDocumentationFile` gerektiriyor; bunun yerine CI'daki `dotnet format --verify-no-changes` kullanılıyor. | Proposed |
| DEC-103 | Admin arayüzü teması sahibin başka projesindeki görsel dilden (slate nötrler, kart/tablo/sidebar kalıpları) uyarlanır, ana renk mavi; grafikler ApexCharts. Ayrıntı: [ui-tema.md](ui-tema.md). Sahibin 2026-11-06 isteği. | Proposed |
| DEC-104 | Admin imajında geçici `apk upgrade --no-cache zlib` (CVE-2026-85091, HIGH). En güncel stable `nginx-unprivileged:1.30.6-alpine-slim` hâlâ `zlib 1.3.2-r0` taşıyor; base imaj düzelince satır kaldırılır (Dependabot güncellemesinde kontrol). | Proposed |
| DEC-105 | `User` global tenant filter'a tabi değildir (Platform Admin'in okulu yok; giriş e-postayla okullar arası arama gerektirir). Identity modülü okul bazlı sorgularda tenant koşulunu açıkça yazar; mimari testi Tenancy ve Identity dışındaki her aggregate'ın `ITenantScoped` olmasını zorlar. | Proposed |
| DEC-106 | Domain event işleyici arayüzünün adı `IDomainEventConsumer<T>`: CA1711 `EventHandler` sonekini yasaklıyor ve analyzer kapatılmıyor. | Proposed |
| DEC-107 | JWT süre doğrulaması `TimeProvider` üzerinden yapılır (`LifetimeValidator`); böylece 15 dk / 7 gün / 30 gün kuralları testte saat ilerletilerek doğrulanır. | Proposed |
| DEC-108 | Tenant yazma koruması: token'da okul varsa sadece o okulun satırları eklenir/değişir/silinir, bir satırın tenant'ı asla değişmez; ihlal `TenantIsolationException` (500, programcı hatası/saldırı). Token'da okul yoksa (Platform Admin) yazma kendi modülünün kontrolündedir. | Proposed |
