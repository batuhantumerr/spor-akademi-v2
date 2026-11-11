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
- [x] P1-13 Angular kabuğu, giriş, ADR-0010 (Accepted, 2026-11-10)
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

- [x] P2-F1 Online aidat ödemesi (iyzico sandbox, webhook'lar, idempotency; DEC-340 – DEC-349). Fake sağlayıcı varsayılan; iyzico adaptörü anahtar verilince ve sadece sandbox'ta. Veli portalı yok: Okul Admini ödeme bağlantısını paylaşır (DEC-348). Gerçek iyzico sandbox'a karşı deneme ve iade akışı açık.
- [x] P2-F2 Hangfire ile arka plan işleri ve iş paneli (DEC-330 – DEC-335)
- [x] P2-F3 Transactional outbox (DEC-300 – DEC-304)
- [x] P2-F4 E-posta bildirimleri (DEC-350 – DEC-358)
- [x] P2-F5 Otomatik anonimleştirme işi (DEC-360 – DEC-368)
- [x] P2-F6 Belge yükleme (S3 uyumlu; yerelde SeaweedFS, DEC-320 – DEC-326)
- [x] P2-F7 Redis ve `HybridCache` ile cache (Valkey; DEC-310 – DEC-316)
- [x] P2-F8 Aidat kuralları: sürümlü kardeş indirimi ve grup değişikliğinde tek aidat (DEC-370 – DEC-376; geçmiş aylar her gece kapanır ve borçları dondurulur; varsayılanlar onay listesinde bölüm 7 ve 9). Kural sürümü olmayan okulda hesap değişmez; kıst yok (DEC-017).

### Sürüm hazırlığı
- [x] P2-R1 Çıkış kriterlerinin otomatik testlerle doğrulanması ([faz-2-cikis-kriterleri.md](faz-2-cikis-kriterleri.md); online ödeme e2e'si, e2e API'si `Staging` + Fake, DEC-241)
- [x] P2-R2 Faz 2 geçişi (sahip onayı 2026-11-11)
- [ ] P2-R3 `v0.2.0` etiketi (sahip)

## Faz 3 — Veli deneyimi (`v0.3.0`)

Faz 2 geçişinde (2026-11-11) ayrıntılandırıldı. Sıra: önce veli hesabı ve API'si (her şey bunun üzerine kurulur), sonra veli uygulaması, gerçek zamanlı bildirimler, raporlar, dil ve erişilebilirlik.

Veli yalnızca **kendi çocuklarını** görür. Bu, okul izolasyonunun yanında ikinci bir yetki sınırıdır ve her uç nokta için testle kanıtlanır.

### 3.1 Veli hesapları (P3-F1)
- [x] P3-01 **Veli rolü.** (DEC-382, DEC-387)
  - `Parent` rolü eklenir; veli kaydı tek okul içinde bir kullanıcı hesabına bağlanır.
  - Veli yalnızca bağlı olduğu öğrencilerin verisini görür. Acil durum kişisi, sağlık raporu ve ödeme iptal gerekçesi bunun dışındadır.
  - Bu sınır için ayrı bir izolasyon test paketi yazılır.
- [x] P3-02 **Davet akışı.** (DEC-383, DEC-384; davet öğrenci ayrıntısındaki veli satırından)
  - Okul Admini, e-postası olan veliyi öğrenci veya veli ekranından davet eder.
  - Veli şifresini 72 saat geçerli, tek kullanımlık bir bağlantıyla belirler (P2-F4 altyapısı).
  - Davet yeniden gönderilebilir ve iptal edilebilir; davet durumu ekranda görünür.
  - Veli silinir ya da son çocuğu okuldan ayrılırsa hesap kapanır.
- [x] P3-03 **Veli API'si** (`/api/v1/parent/...`; DEC-385, DEC-386, DEC-388).
  - Çocuklarım, haftalık program, yoklama geçmişi, borçlar, ödemeler ve duyurular.
  - Borç tutarı `DueCharges` ile hesaplanır.
  - Veli kendi borcu için online ödeme başlatabilir. P2-F1 akışı kullanılır; idempotency ve borç başına tek açık deneme kuralı geçerlidir.

### 3.2 Veli uygulaması (P3-F2, DEC-024)
- [x] P3-04 **`web/parent` iskeleti.** (ADR-0012, DEC-377 – DEC-380)
  - React, Vite ve TypeScript; Tailwind v4 admin ile aynı tema token'larını kullanır.
  - TanStack Query, React Router ve OpenAPI'den üretilen istemci.
  - CI'da `parent` job'u ve nginx imajı eklenir.
  - Paket seçimleri bir ADR'de kayda geçer.
- [x] P3-05 **Oturum.** Giriş, bellekte tutulan access token ve refresh çerezi, davet kabulü, şifre sıfırlama ve çıkış. (DEC-389, DEC-390)
- [x] P3-06 **Ekranlar** (önce mobil). Çocuklarım, program, yoklama geçmişi, borçlar ve online ödeme, ödeme geçmişi, duyurular. (DEC-391 – DEC-393)
- [x] P3-07 **PWA.** (DEC-394, DEC-395)
  - Manifest ve kurulabilirlik.
  - Service worker yalnızca statik dosyaları önbelleğe alır; API yanıtları ve kişisel veri önbelleğe alınmaz.
  - Çevrimdışı sayfası.

### 3.3 Gerçek zamanlı (P3-F3)
- [ ] P3-08 **SignalR.**
  - Kimlik doğrulama JWT ile yapılır; yayın grupları okul, rol, grup ve veli bazındadır.
  - Mesajlar commit'ten sonra outbox üzerinden yayınlanır.
  - Anlık bildirim gönderilen durumlar:
    - Duyuru yayımlanınca antrenöre ve veliye.
    - Yoklama kaydedilince veliye ("bugün antrenmana katıldı" veya "katılmadı").
    - Ödeme alınınca veliye.
  - Valkey backplane isteğe bağlıdır.
  - Bağlantı koptuğunda istemci yeniden bağlanır ve listeyi yeniler.

### 3.4 Raporlar (P3-F4)
- [ ] P3-09 **Okul Admini raporları.**
  - Devam oranı: grup, branş ve antrenör bazında, aylık.
  - Doluluk eğilimi.
  - Gelir: tahakkuk ve tahsilat, kardeş indiriminin etkisi, geciken borçlar.
  - Grafikler erişilebilir olur; her rapor CSV olarak dışa aktarılabilir.

### 3.5 Dil, erişilebilirlik ve diğerleri
- [ ] P3-10 **Çoklu dil (TR/EN)**, admin ve veli uygulamasında.
  - API hata mesajları `Accept-Language` başlığına göre döner.
  - Tarih ve para biçimleri seçilen dile uyar; varsayılan dil Türkçe.
- [ ] P3-11 **WCAG 2.2 AA denetimi** (P3-F6).
  - Önemli ekranların listesi çıkarılır.
  - axe ile otomatik tarama, ardından klavye ve ekran okuyucuyla elle kontrol.
  - Bulgular düzeltilir; rapor `docs/` altına yazılır.
- [x] P3-12 **Okul başına alt alan adı değerlendirmesi** (P3-F7). Sonuç bir ADR olarak kaydedilir. (ADR-0013, DEC-381: şimdi yapılmaz)
- [ ] P3-13 **Online ödeme iadesi.**
  - Okul Admini, ödemenin tamamını sağlayıcı üzerinden iade eder ve gerekçe girer.
  - Fazla ödeme ve peşin ödenmiş kaydın erken bitirilmesi durumlarını da kapsar (onay listesi, soru 14).

### Sürüm hazırlığı
- [ ] P3-R1 **Çıkış kriterlerinin e2e testleriyle doğrulanması.**
  - Veli daveti kabul eder, çocuğunun yoklamasını görür ve borcunu online öder.
  - Duyuru veliye anlık olarak ulaşır.
  - Uygulama iki dilde de çalışır.
  - WCAG raporunda açık kritik bulgu kalmaz.
- [ ] P3-R2 Faz 3 geçişi ve `v0.3.0` etiketi (sahip)
