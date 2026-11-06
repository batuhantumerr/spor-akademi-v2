# Spor Akademi — agent rehberi

Tek kaynak: [docs/proje-dokumani.md](docs/proje-dokumani.md). Ürün, domain, mimari, API, hata kodları, karar günlüğü ve roadmap oradadır. Bu dosya özet ve bu repoya özgü farklardır.

## Önce oku
- İşe başlarken: [docs/roadmap.md](docs/roadmap.md) (Faz 1 ilerleme durumu burada işaretlenir).
- Özellik: proje dokümanı Ek A (hikayeler), Ek B (domain), Ek D (API), Ek E (hata kodları).
- Yeni kararlar: [docs/karar-gunlugu-v2.md](docs/karar-gunlugu-v2.md). Arayüz: [docs/ui-tema.md](docs/ui-tema.md).

## Dil
Sahiple ve dokümanlarda Türkçe; kod, isimler, enum'lar, commit'ler İngilizce. Teknik terimler çevrilmez.

## Mimari (özet, ayrıntı dokümanın 5. bölümünde)
- Modüler monolit + Clean Architecture: `Api → Application, Infrastructure`, `Infrastructure → Application`, `Application → Domain`. Dokuz modül katman projelerinde klasördür.
- MediatR, AutoMapper, dispatcher yok. Endpoint `ICommandHandler`/`IQueryHandler`'ı doğrudan alır; decorator sırası Logging → Validation → Transaction.
- Beklenen hata `Result`/`Error`, beklenmeyen exception. API sabit hata kodu döner.
- Multi-tenancy: `TenantId` + global filter + yazma koruması + bileşik FK + tenant'lı unique index. Başka okulun kaydı `404`.
- Zaman `TimeProvider`'dan (`DateTime.Now/UtcNow` yasak); ID'ler `Guid.CreateVersion7()`.
- Kayıt kilitleri sadece `IEnrollmentLocks`: önce öğrenci, sonra grup, `lock_timeout` 5 sn.

## Test
- Önce test, kırmızıyı gör. `[Trait("Story", "US-xx")]`. Shouldly (FluentAssertions yasak). DB'ye dokunan kod Testcontainers ile gerçek PostgreSQL'de.
- "Bitti" demeden önce `dotnet test --solution SporAkademi.slnx` ve `npm test` çalıştır.

## Komutlar
- `bash scripts/setup-env.sh` (bir kez), `docker compose up -d --wait`, `bash scripts/ef.sh database update`, `bash scripts/run-api.sh`, `cd web/admin && npm start`.
- Migration: `bash scripts/ef.sh migrations add <Name>`; her zaman migrator rolüyle.

## Güvenlik
`.env` hiçbir yoldan okunmaz. `printenv`, `docker inspect`, `docker compose config`, `dotnet user-secrets list` çalıştırılmaz. Loglarda kişisel veri yok. Sadece sentetik veri.

## GitHub iş akışı
Issue → branch (`<tür>/<issue-no>-<kısa-açıklama>`, güncel `main`'den) → küçük commit'ler (Conventional Commits, AI attribution yok) → PR (`Closes #n`) → dört check (`backend`, `frontend`, `repo-checks`, `images`) → squash merge. Rebase ve force push yok.

## Hafta sonu otonom çalışma istisnaları (sahibin 2026-11-06 kararı)
- Agent, dört check yeşil ve kodu yazmamış ayrı bir agent incelemesi yapılmışsa PR'ı kendisi squash merge eder.
- Paket, ADR ve DEC kararlarını agent gerekçesiyle verir, "Proposed" olarak işaretler ve [docs/pazartesi-onay-listesi.md](docs/pazartesi-onay-listesi.md)'ye ekler.
- Faz geçişi, sürüm etiketi ve Deploy bloğu sahibe kalır.
