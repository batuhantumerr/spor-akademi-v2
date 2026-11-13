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
- [x] P1-40 Faz 1 geçişi ve `v0.1.0` etiketi (sahip; etiket 2026-11-13, ae38efd)

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
- [x] P2-R3 `v0.2.0` etiketi (sahip, 2026-11-13; 6eb0f17)

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
- [x] P3-08 **SignalR.** (DEC-396 – DEC-405)
  - Kimlik doğrulama JWT ile yapılır; yayın grupları okul, rol, grup ve veli bazındadır.
  - Mesajlar commit'ten sonra outbox üzerinden yayınlanır.
  - Anlık bildirim gönderilen durumlar:
    - Duyuru yayımlanınca antrenöre ve veliye.
    - Yoklama kaydedilince veliye ("bugün antrenmana katıldı" veya "katılmadı").
    - Ödeme alınınca veliye.
  - Valkey backplane isteğe bağlıdır.
  - Bağlantı koptuğunda istemci yeniden bağlanır ve listeyi yeniler.

### 3.4 Raporlar (P3-F4)
- [x] P3-09 **Okul Admini raporları.** (DEC-406 – DEC-413)
  - Devam oranı: grup, branş ve antrenör bazında, aylık.
  - Doluluk eğilimi.
  - Gelir: tahakkuk ve tahsilat, kardeş indiriminin etkisi, geciken borçlar.
  - Grafikler erişilebilir olur; her rapor CSV olarak dışa aktarılabilir.

### 3.5 Dil, erişilebilirlik ve diğerleri
- [x] P3-10 **Çoklu dil (TR/EN)**, admin ve veli uygulamasında. (DEC-426 – DEC-431)
  - API hata mesajları `Accept-Language` başlığına göre döner.
  - Tarih ve para biçimleri seçilen dile uyar; varsayılan dil Türkçe.
- [x] P3-11 **WCAG 2.2 AA denetimi** (P3-F6). Rapor: [erisilebilirlik-denetimi.md](erisilebilirlik-denetimi.md). (DEC-436 – DEC-443; ekran okuyucuyla elle tur sahipte)
  - Önemli ekranların listesi çıkarılır.
  - axe ile otomatik tarama, ardından klavye ve ekran okuyucuyla elle kontrol.
  - Bulgular düzeltilir; rapor `docs/` altına yazılır.
- [x] P3-12 **Okul başına alt alan adı değerlendirmesi** (P3-F7). Sonuç bir ADR olarak kaydedilir. (ADR-0013, DEC-381: şimdi yapılmaz)
- [x] P3-13 **Online ödeme iadesi.** (DEC-416 – DEC-425)
  - Okul Admini, ödemenin tamamını sağlayıcı üzerinden iade eder ve gerekçe girer.
  - Fazla ödeme ve peşin ödenmiş kaydın erken bitirilmesi durumlarını da kapsar (onay listesi, soru 14).

### Sürüm hazırlığı
- [x] P3-R1 **Çıkış kriterlerinin e2e testleriyle doğrulanması.** (`web/admin/e2e/parent/phase3-exit.e2e.ts`)
  - Veli daveti kabul eder, çocuğunun yoklamasını görür ve borcunu online öder.
  - Duyuru veliye anlık olarak ulaşır.
  - Uygulama iki dilde de çalışır.
  - WCAG raporunda açık kritik bulgu kalmaz.
- [x] P3-R2 Faz 3 geçişi (sahip onayı 2026-11-13)
- [x] P3-R3 `v0.3.0` etiketi (sahip, 2026-11-13; `main` 5260643, Faz 0 – Faz 3)

## Faz 4 — Production sağlamlaştırma (`v0.4.0`)

Ayrıntılar Faz 3 geçişinde (2026-11-13) yazıldı. Amaç: platform gerçek veriye emanet edilecek kadar güvenli, gözlemlenebilir ve hızlı olur. Faz 4 KVKK gereksinimleri tamamlanıp sahip onaylayana kadar yalnız sentetik veri kullanılır (DEC-031). Deploy bloğu (sunucu, Caddy, şifreli yedek betiği) sahipte kalır; buradaki işler yerelde ve CI'da kanıtlanır. Yeni kararlar DEC-500'den başlar.

Migration ekleyen işler (RLS → audit log → KVKK başvuruları) art arda yapılır, diğerleri bunlara paralel ilerler.

### 4.1 Güvenlik (P4-F1; NFR-SEC-01, -13, -14)
- [x] P4-01 **Güvenlik başlıkları ve tam CSP.** (DEC-500 – DEC-502)
  - API yanıtlarında güvenlik başlıkları.
  - Admin ve veli nginx imajlarında `frame-ancestors` yerine tam CSP: `default-src 'self'`, SignalR için `connect-src`, belge küçük resmi için depo origin'i `img-src`'de.
  - HSTS yalnız HTTPS'te ve Production'da; `check-security-headers.sh` yeni başlıkları da doğrular.
- [x] P4-02 **API rate limiting.** Kullanıcı bazında genel sınır ve okul bazında yazma sınırı; `429`, `Retry-After` ve sabit hata kodu. Faz 1'deki giriş sınırları korunur. (DEC-503 – DEC-507)
- [ ] P4-03 **OWASP Top 10 (2021) ve ASVS L2 incelemesi.**
  - Her kategori için bulgu, kanıt ve düzeltme `docs/guvenlik-kontrol-listesi.md`'ye yazılır; yüksek bulgu açık kalmaz.
  - Trivy checksum'ının workflow'a sabitlenmesi yeniden değerlendirilir.

### 4.2 Veri koruma (P4-F2 – P4-F4)
- [x] P4-04 **PostgreSQL Row Level Security** (ikinci izolasyon katmanı) (DEC-520 – DEC-528).
  - Uygulama rolü her bağlantıda okul bağlamını (`set_config`) taşır; tenant'lı her tabloda `FORCE ROW LEVEL SECURITY` ve politika bulunur.
  - Platform Admin, arka plan işleri ve okullar arası giriş araması için açık ve test edilmiş istisnalar.
  - Kanıt: EF global filter'ı kapalıyken başka okulun satırı okunamaz ve yazılamaz.
- [x] P4-05 **Kişisel veri audit log'u.** (DEC-530 – DEC-537)
  - Öğrenci, veli, sağlık belgesi ve kullanıcı verisinin okunması ve değiştirilmesi kaydedilir: kim, ne zaman, hangi kayıt, hangi alanlar (değerler değil).
  - Audit tablosuna uygulama rolü yalnız ekleme yapabilir; saklama süresi var; Okul Admini için listeleme ekranı.
- [x] P4-06 **KVKK ilgili kişi başvuruları** (NFR-PRV-05, -06) (DEC-538 – DEC-547).
  - Bir öğrencinin ve velisinin verisi makinece okunur biçimde dışa aktarılır (JSON ve belgeler).
  - Talep üzerine silme: anonimleştirme altyapısıyla, saklama süresinden bağımsız, audit kaydıyla.
  - Aydınlatma metni taslağı ve arayüzde bağlantısı (hukuki teyit sahipte).
  - Gerçek verinin barındırma konumu için ADR (NFR-PRV-07; yurt dışı aktarım koşulları). Seçenekler ve öneri agent tarafından yazılır, karar sahipte.

### 4.3 Operasyon (P4-F5 – P4-F8)
- [ ] P4-07 **OpenTelemetry ve hata takibi** (NFR-OBS-04).
  - Trace (ASP.NET Core, HttpClient, EF/Npgsql, Hangfire, outbox), metrik ve log OTLP ile; kişisel veri ve token taşınmaz.
  - Yerelde compose profiliyle dashboard ve alarm kuralları.
  - Sentry ile hata takibi: SDK entegre edilir ve testle doğrulanır; DSN ortam yapılandırmasından gelir, yoksa gönderim kapalıdır (anahtar repoda yok, gerçek DSN Deploy bloğunda).
- [ ] P4-08 **k6 ile yük testi ve performans bütçesi** (NFR-PRF-01, -02).
  - Okuma p95 < 300 ms, yazma p95 < 500 ms; demo verisiyle yerelde ölçülür, sonuçlar `docs/`'a yazılır.
  - N+1 sorgular ve eksik index'ler düzeltilir; regresyon için sorgu sayısı testleri.
  - Durumsuzluk (NFR-SCL-01): yük testi proxy arkasında iki API örneğiyle de koşar; oturum, SignalR ve rate limit sayaçlarının davranışı belgelenir.
- [ ] P4-09 **Yedekten geri yükleme tatbikatı** (NFR-REL-04).
  - Yerel yedek alma ve geri yükleme betikleri; WAL arşiviyle point-in-time recovery (15 dakikalık RPO).
  - Tatbikatta tam yedekten ve belirli bir ana (PITR) geri dönüş denenir.
  - Tatbikat ölçülür ve süresiyle belgelenir; production yedeği Deploy bloğunda.
  - RLS notu (DEC-528): `FORCE`'lu tablolarda `pg_dump` sahip rolüyle `--enable-row-security` ve `PGOPTIONS='-c app.maintenance=on'` ister (ya da `BYPASSRLS`'li ayrı bir yedek rolü); tatbikat yedeğin bütün okulları içerdiğini doğrular.
- [ ] P4-10 **Feature flag'ler** (kademeli yayın).
  - Okul bazında açılıp kapanan bayraklar; Platform Admin yönetir, istemciler bayrakları okur.
  - Kapalı bayraklı uç nokta `404` döner; bayrak değişikliği cache'i geçersiz kılar.

### 4.4 Faz 3'ten kalan takip işleri
- [ ] P4-11 **Test ve CI iyileştirmeleri.**
  - `wcag.ts`'teki `waitForTimeout(500)` koşul beklemesiyle değişir; a11y projeleri ayrı CI job'ına taşınır.
  - Ay sınırını aşan iade testi (DEC-420); çıkış ile refresh grace replay'i arasında barrier entegrasyon testi.

### Sürüm hazırlığı
- [ ] P4-R1 **Çıkış kriterlerinin doğrulanması** (`docs/faz-4-cikis-kriterleri.md`).
  - OWASP incelemesinde açık yüksek bulgu yok; güvenlik başlıkları otomatik kontrol ediliyor.
  - RLS, uygulama filtresi kapalıyken bile okullar arası okuma ve yazmayı engelliyor.
  - Kişisel veri erişimi audit log'da; ilgili kişinin verisi dışa aktarılıp silinebiliyor.
  - Trace ve metrikler dashboard'da görünüyor; yük testi bütçe içinde ve iki API örneğiyle çalışıyor; geri yükleme tatbikatı süresiyle belgeli; bir özellik okul bazında bayrakla açılıp kapanıyor.
- [ ] P4-R2 Faz 4 geçişi ve `v0.4.0` etiketi (sahip)
