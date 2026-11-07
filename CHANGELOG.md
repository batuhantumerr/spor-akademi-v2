# Changelog

Bu dosya [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) biçimini ve [SemVer](https://semver.org/) sürümlemesini izler. Sürüm etiketi (`v0.1.0`) ve faz geçişi sahibe aittir; aşağıdaki maddeler etiketlenene kadar `Unreleased` altında durur.

## [Unreleased]

### Added

#### Faz 0 — Temel
- Backend (Domain, Application, Infrastructure, Api) ve Angular 22 iskeleti, dört test projesi.
- Docker Compose (PostgreSQL 18.6, Seq), migrator/uygulama rolleri, `scripts/ef.sh`, `scripts/run-api.sh`.
- `/health/live`, `/health/ready`, ilk migration; CI (`backend`, `frontend`, `repo-checks`, `images`, `publish`), API/admin/migrator imajları, NuGet kilit dosyaları, Dependabot.

#### Faz 1 — MVP
- **Platform:** entity/aggregate yapı taşları, value object'ler (e-posta, telefon, slug, para, zaman aralığı, yıl-ay), handler pipeline'ı (loglama, doğrulama, transaction dekoratörleri), `Result`/ProblemDetails hata modeli, korelasyon kimlikleri, mimari testleri.
- **Kalıcılık:** modül başına PostgreSQL şemaları, denetim alanları, unit of work (kilit zaman aşımı, eşzamanlılık çakışması, kısıt ihlallerinin beklenen hatalara çevrilmesi), xmin ile iyimser kilit.
- **Çok kiracılık:** okul bazlı sorgu filtreleri, yazma koruması, `(tenant_id, id)` bileşik yabancı anahtarlar; okullar arası izolasyon test paketi (her uç nokta başka okulun kimlikleriyle çağrılır, listeler sızıntıya karşı taranır).
- **Kimlik:** giriş ve hesap kilitleme, bellekte tutulan erişim token'ı, HttpOnly çerezde dönen refresh token (yeniden kullanım tespiti), ilk girişte şifre değiştirme, rol bazlı yetki (varsayılan reddet).
- **Okul kurulumu:** okul ve ilk yöneticisinin oluşturulması (Platform Admin), branşlar, tesisler, antrenörler.
- **Gruplar:** grup oluşturma, haftalık program (antrenör çakışması hata, tesis çakışması uyarı), düzenleme ve arşiv, antrenör ana ekranı.
- **Öğrenciler:** veli ile kayıt, düzenleme, ayrılma, arama ve listeleme; gruba kayıt (kapasite kilidi), kaydı sonlandırma, antrenör yoklama listesi.
- **Yoklama:** antrenörün telefondan yoklama alması, antrenör için 48 saatlik düzeltme penceresi (Okul Admini sınırsız), grup aylık yoklama tablosu ve öğrenci devam geçmişi.
- **Aidat:** grup ücretleri (aydan itibaren geçerli), ödeme kaydı ve gerekçeli iptal, ödenmemiş aidat listesi ve toplamları; tutarlar hiçbir yerde kayan noktalı sayıya dönüşmez.
- **Duyurular:** grup duyurusu yayımlama, antrenörün duyuruları görmesi.
- **Yönetici paneli:** tek istekte özet (öğrenci/grup sayıları, doluluk, tahsilat, bekleyen aidat, devam oranı) ve ECharts grafikleri (6 aylık tahsilat/tahakkuk, 8 haftalık devam, branşa göre kayıtlar, en dolu gruplar); açık/koyu tema, erişilebilir grafik özetleri ve veri tabloları.
- **Arayüz:** Angular uygulama kabuğu, rol bazlı yerleşim, açık/koyu tema, bildirimler, onay pencereleri, çakışmada düzenlemeleri koruyan uyarı; OpenAPI belgesi ve üretilen Angular istemcisi.
- **Demo verisi:** `bash scripts/seed-demo.sh` iki sentetik okul ve ~92 günlük geçmiş üretir; yalnızca yerel veritabanında ve Production dışı ortamda çalışır.
- **Güvenlik:** MVP güvenlik kontrol listesi ve otomatik kontroller (gitleaks taraması, `Cache-Control: no-store`, giriş/refresh/şifre değiştirme için IP + yol başına dakikada 10 istek sınırı, log gizliliği, CORS'un kapalı olduğunu doğrulayan test), Trivy imaj taraması. CSP/güvenlik başlıkları (NFR-SEC-13) ve HTTPS/HSTS (NFR-SEC-02) MVP'de yoktur; Faz 4 / Deploy bloğunda yapılacak.

#### Faz 2 — İş mantığı derinliği
- **Transactional outbox (P2-F3):** dış yan etkiler iş değişikliğiyle aynı transaction'da `outbox.messages`'a yazılır ve commit'ten sonra işlenir (`FOR UPDATE SKIP LOCKED`, jitter'lı üstel geri çekilme, dead-letter, saklama süresi, `/health/ready`'de `outbox` kontrolü). İlk kullanım: öğrenci ayrıldığında `students.student-left.v1` mesajı (DEC-300 – DEC-304).
- **Cache (P2-F7):** okuma ağırlıklı ve seyrek değişen okumalar (branş ve tesis listeleri, okulun saat dilimi, School Admin dashboard'u) `HybridCache` ile cache'lenir: L1 bellekte, L2 isteğe bağlı olarak Valkey'de (`ConnectionStrings:Cache`; yoksa sadece bellek). Her anahtar ve etiket okul ID'sini taşır, yazmalar commit'ten sonra otomatik invalidation yapar, Valkey çökerse istekler veritabanından devam eder. Docker Compose'a isteğe bağlı `valkey` servisi eklendi (`--profile cache`); `VALKEY_PASSWORD` olmayan eski `.env` dosyaları değişmeden çalışır, `setup-env.sh` eksik anahtarları ekler (DEC-310 – DEC-316).
- **Arka plan işleri ve iş paneli (P2-F2):** Hangfire (PostgreSQL deposu, `hangfire` şeması migrator rolüyle kurulur, API'nin DDL yetkisi yok) API sürecinde çalışır; outbox teslimi ve temizliği artık tekrarlayan Hangfire işleri (`Outbox:Scheduler=Worker` eski polling worker'ı seçer), bitmiş oturumların refresh token'ları her gece okul okul silinir. Okul verisine dokunan işler her okul için ayrı scope'ta o okulun bağlamında çalışır. Platform Admin için `/jobs` iş paneli: admin uygulamasındaki "İş paneli" sayfası 15 dakikalık, `HttpOnly`/`SameSite=Strict` bir panel çerezi alır; panel varsayılan olarak salt okunur, işlemler açıksa antiforgery ister. `/health/ready`'de `jobs` kontrolü (heartbeat yoksa Degraded) (DEC-330 – DEC-335).

### Security
- Admin imajında zlib ve musl güvenlik yükseltmeleri (DEC-104, DEC-202; öneri aşamasında).
