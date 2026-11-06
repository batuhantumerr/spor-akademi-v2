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
- [ ] P1-13 Angular kabuğu, giriş, ADR-0010
- [x] P1-14 OpenAPI ve Angular client

### 1.2 Okul kurulumu ve gruplar
- [x] P1-15 US-06 Branşlar
- [x] P1-16 US-07 Tesisler
- [x] P1-17 US-08 Antrenörler
- [ ] P1-18 US-09 Grup oluşturma
- [ ] P1-19 US-10 Haftalık program
- [ ] P1-20 US-11 Grup düzenleme ve arşiv
- [ ] P1-21 US-12 Antrenör ana ekranı

### 1.3 Öğrenciler ve kayıt
- [ ] P1-22 US-13 Öğrenci kaydı
- [ ] P1-23 US-14 Antrenör görünümü
- [ ] P1-24 US-15 Ayrıldı
- [ ] P1-25 US-16 Arama ve listeleme
- [ ] P1-26 US-17 Kayıt
- [ ] P1-27 US-18 Kaydı sonlandırma

### 1.4 Yoklama
- [ ] P1-28 US-19 Yoklama alma
- [ ] P1-29 US-20 Düzeltme (48 saat)
- [ ] P1-30 US-21 Geçmiş

### 1.5 Aidat ve duyuru
- [ ] P1-31 US-22 Aidat tanımı
- [ ] P1-32 US-23 Ödeme kaydı ve iptali
- [ ] P1-33 US-24 Ödenmemiş borçlar
- [ ] P1-34 US-25 Duyurular

### Sürüm hazırlığı
- [ ] P1-35 Tenant izolasyon test paketi
- [ ] P1-36 Uçtan uca testler (Playwright)
- [ ] P1-37 MVP güvenlik kontrol listesi
- [ ] P1-38 Demo verisi seed betiği
- [ ] P1-39 CHANGELOG (etiket sahipte)
- [ ] P1-40 Faz 1 geçişi (sahip)
