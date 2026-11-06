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
