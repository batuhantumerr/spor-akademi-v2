# Yol haritası — ilerleme

Kapsamın tamamı ve maddelerin açıklamaları: [proje dokümanı, Ek G](proje-dokumani.md). Bu dosya bu repodaki (spor-akademi-v2) ilerlemeyi işaretler. Her PR kendi maddesini burada işaretler.

## Faz 0 — Temel

- [x] Repo, kurallar, CLAUDE.md, dokümanlar
- [x] Backend iskeleti (4 katman, 4 test projesi), Angular 22 iskeleti
- [x] Docker Compose (PostgreSQL 18.6, Seq), rol init script'i, `scripts/ef.sh`, `scripts/run-api.sh`
- [x] `/health/live`, `/health/ready`; ilk migration
- [x] CI (`backend`, `frontend`, `repo-checks`, `images`, `publish`), API/admin/migrator imajları
- [x] NuGet lock dosyaları, Dependabot
- [ ] `v0.0.1` etiketi ve faz geçişi (sahip)

## Faz 1 — MVP (`v0.1.0`)

### 1.1 Yapı taşları ve kimlik doğrulama
- [x] P1-01 Domain yapı taşları
- [x] P1-02 Value object'ler
- [x] P1-03 Application pipeline'ı
- [x] P1-04 Kalıcılık temeli
- [x] P1-05 Tenancy
- [x] P1-06 Hata yönetimi
- [x] P1-07 Mimari testleri
- [x] P1-08 Entegrasyon test altyapısı
- [x] P1-09 US-03 Giriş
- [x] P1-10 US-04 Refresh ve çıkış
- [x] P1-11 US-05 İlk giriş ve şifre değiştirme
- [x] P1-12 US-01 Okul oluşturma
- [x] P1-13 Angular kabuğu, giriş, ADR-0010 (ADR-0010 Proposed, sahip onayı bekliyor)
- [x] P1-14 OpenAPI ve Angular client

### 1.2 Okul kurulumu ve gruplar
- [x] P1-15 US-06 Branşlar
- [x] P1-16 US-07 Tesisler
- [x] P1-17 US-08 Antrenörler
- [x] P1-18 US-09 Grup oluşturma
- [x] P1-19 US-10 Haftalık program
- [x] P1-20 US-11 Grup düzenleme ve arşiv
- [x] P1-21 US-12 Antrenör ana ekranı

### 1.3 Öğrenciler ve kayıt
- [x] P1-22 US-13 Öğrenci kaydı
- [x] P1-23 US-14 Antrenör görünümü
- [x] P1-24 US-15 Ayrıldı
- [x] P1-25 US-16 Arama ve listeleme
- [x] P1-26 US-17 Kayıt
- [x] P1-27 US-18 Kaydı sonlandırma

### 1.4 Yoklama
- [x] P1-28 US-19 Yoklama alma
- [x] P1-29 US-20 Düzeltme (48 saat)
- [x] P1-30 US-21 Geçmiş

### 1.5 Aidat ve duyuru
- [x] P1-31 US-22 Aidat tanımı
- [x] P1-32 US-23 Ödeme kaydı ve iptali
- [x] P1-33 US-24 Ödenmemiş borçlar
- [x] P1-34 US-25 Duyurular

### Sürüm hazırlığı
- [x] P1-35 Tenant izolasyon test paketi
- [x] P1-36 Uçtan uca testler (Playwright)
- [x] P1-37 MVP güvenlik kontrol listesi
- [x] P1-38 Demo verisi seed betiği
- [x] P1-39 CHANGELOG (etiket sahipte)
- [ ] P1-40 Faz 1 geçişi (sahip)

## Faz 2 — İş mantığı derinliği (`v0.2.0`)

- [ ] P2-F1 Online aidat ödemesi (iyzico sandbox, webhook'lar, idempotency)
- [x] P2-F2 Hangfire ile arka plan işleri ve iş paneli (DEC-330 – DEC-335)
- [x] P2-F3 Transactional outbox (DEC-300 – DEC-304)
- [ ] P2-F4 E-posta bildirimleri
- [ ] P2-F5 Otomatik anonimleştirme işi
- [x] P2-F6 Belge yükleme (S3 uyumlu; yerelde SeaweedFS, DEC-320 – DEC-326)
- [x] P2-F7 Redis ve `HybridCache` ile cache (Valkey; DEC-310 – DEC-316)
- [ ] P2-F8 Aidat kuralları (kardeş indirimi, dönem ortası değişiklik)
