# Spor Akademi

Spor okulları için çok kiracılı (multi-tenant) yönetim platformu: okullar, branşlar, tesisler, gruplar, öğrenciler, kayıtlar, yoklama ve aidat.

- Backend: .NET 10, ASP.NET Core, EF Core, PostgreSQL 18
- Admin arayüzü: Angular 22, Tailwind CSS, Apache ECharts
- Veli uygulaması (Faz 3): React 19, Vite, TanStack Query, Tailwind CSS ([ADR-0012](docs/adr/0012-veli-uygulamasi-teknoloji-yigini.md))
- Mimari: Clean Architecture katmanlarıyla modüler monolit ([proje dokümanı](docs/proje-dokumani.md))

## Gereksinimler

- .NET SDK 10.0.401+ (`global.json`)
- Node.js 26 (`web/admin/.nvmrc`, `web/parent/.nvmrc`)
- Docker (Compose v2)
- Bash (Windows'ta Git Bash) ve `openssl`

## Yerelde çalıştırma

```bash
bash scripts/setup-env.sh          # .env'i rastgele şifrelerle oluşturur (bir kez)
docker compose up -d --wait        # PostgreSQL ve Seq (Valkey ve belge deposu isteğe bağlı, aşağıya bakın)
dotnet tool restore
bash scripts/ef.sh database update # migration'ları migrator rolüyle uygular
bash scripts/run-api.sh            # API: http://127.0.0.1:5080
```

Başka bir terminalde:

```bash
cd web/admin
npm ci
npm start                          # http://localhost:4200 (/api proxy'si API'ye gider)
```

- Sağlık kontrolleri: `/health/live`, `/health/ready` (belge deposuna ulaşılamazsa `ready` 200 ama `Degraded` döner, DEC-326)
- Loglar (Seq): http://127.0.0.1:5341
- İş paneli (Hangfire, sadece Platform Admin): admin uygulamasında Platform Admin olarak giriş yapın, menüden **İş paneli** → **Paneli yeni sekmede aç**. Panel http://localhost:4200/jobs adresinde açılır (`/jobs` proxy'si API'ye gider). **Panel şimdilik sadece bu yerel dev proxy'siyle çalışır:** üretimdeki admin imajının nginx'i `/jobs`'u API'ye yönlendirmez, bu Deploy bloğunda reverse proxy'de yapılacak; doğrudan adrese gitmek `401` verir, çünkü panel sadece bu düğmenin aldığı 15 dakikalık çerezle açılır. Development'ta işlemler (yeniden dene, sil, şimdi çalıştır) açıktır, diğer ortamlarda panel salt okunurdur (`Jobs__Dashboard__ReadOnly`). Hangfire sunucusu API içinde çalışır; kapatmak için `Jobs__ServerEnabled=false` (DEC-330 – DEC-333).

### Veli uygulaması (P3-04 – P3-06)

Veliler davet e-postasındaki bağlantıyla (`/reset-password#token=…`) şifrelerini belirler, sonra giriş yapar: Çocuklarım, çocuk ayrıntısı (aidatlar ve online ödeme, haftalık program, yoklama, ödemeler, duyurular). Yerelde bir veli hesabı için Okul Admini olarak öğrencinin velisini davet edin; e-posta Mailpit'e düşer (aşağıdaki E-posta bölümü, `mail` profili). Bağlantıların veli uygulamasına gitmesi için API'yi `Email__ParentAppBaseUrl=http://localhost:4201/` ile başlatın; yoksa bağlantı admin uygulamasının aynı sayfasına gider. Fake ödeme sağlayıcısının sandbox sayfası ve ödeme sonucu sayfası veli uygulamasında da vardır (`/sandbox-odeme`, `/odeme-sonucu`).

```bash
cd web/parent
npm ci
npm run dev                        # http://localhost:4201 (/api ve /health proxy'si API'ye gider, vite.config.ts)
```

**PWA (P3-07):** service worker yalnız production build'de kaydolur ve tarayıcılar onu yalnız güvenli bağlamda (HTTPS veya `localhost`) çalıştırır; `npm run dev` worker'sız çalışır. Yerelde denemek için:

```bash
cd web/parent
npm run build && npm run preview   # http://localhost:4201 (localhost güvenli bağlam sayılır; /api proxy'si yok)
```

Tarayıcının geliştirici araçlarında Application → Service workers / Cache storage / Manifest bölümleri kontrol edilir; Network → Offline ile sayfa yenilenince "Bağlantı yok" ekranı görünmelidir. Önbellekte yalnız `sa-parent-<sürüm>` adlı tek önbellek ve build dosyaları olmalı, hiçbir `/api` yolu olmamalı. Oturumlu bir deneme için API ile birlikte `docker build -f docker/parent/Dockerfile` imajı veya e2e ortamı kullanılır. Eski bir worker'ı kaldırmak: Application → Service workers → Unregister ve Clear site data (DEC-394, DEC-395).

İki uygulama da istemcisini commit edilen OpenAPI belgesinden (`openapi/spor-akademi.json`) çalıştırma/build/test öncesi otomatik üretir. Tema token'ları admin'den kopyalanır; `bash scripts/check-theme-tokens.sh` sapmayı yakalar (DEC-378).

### Cache (P2-F7)

Branş ve tesis listeleri, okulun saat dilimi ve dashboard `HybridCache` ile cache'lenir (DEC-310 – DEC-316). Bellek içi cache her zaman vardır; Valkey (L2) isteğe bağlıdır ve compose'un `cache` profilindedir, yani düz `docker compose up` onu başlatmaz ve `VALKEY_PASSWORD` istemez:

```bash
bash scripts/setup-env.sh                       # eski bir .env'e sadece eksik VALKEY_* anahtarlarını ekler
docker compose --profile cache up -d --wait     # PostgreSQL, Seq ve Valkey
bash scripts/run-api.sh                         # Valkey çalışıyorsa ConnectionStrings__Cache'i ayarlar
```

`setup-env.sh` var olan `.env`'in değerlerini değiştirmez ve yazdırmaz. `VALKEY_PASSWORD` olmadan Valkey başlamayı reddeder (hata mesajı `docker compose --profile cache logs valkey`'de); diğer servisler, `scripts/e2e.sh` ve CI'daki `e2e` işi etkilenmez. `ConnectionStrings:Cache` yoksa (ör. testlerde) sadece bellek içi cache kullanılır; Valkey kapanırsa API veritabanından çalışmaya devam eder.

### E-posta (P2-F4)

Hoş geldin, şifre sıfırlama ve aidat hatırlatma e-postaları outbox'tan, commit sonrası gönderilir (DEC-350 – DEC-358). SMTP ayarı yoksa (`Email__Smtp__Host`) API e-posta göndermez, sadece şablon adını loglar. Yerelde e-postaları görmek için isteğe bağlı Mailpit (compose'un `mail` profili, sadece `127.0.0.1`, hiçbir şeyi dışarı iletmez):

```bash
bash scripts/setup-env.sh                       # eski bir .env'e sadece eksik MAILPIT_* port anahtarlarını ekler
docker compose --profile mail up -d --wait      # PostgreSQL, Seq ve Mailpit
bash scripts/run-api.sh                         # Mailpit çalışıyorsa API'nin SMTP'sini ona yönlendirir
```

Gelen kutusu: http://127.0.0.1:58025. E-postalardaki bağlantılar `Email__AppBaseUrl`'e (varsayılan `http://localhost:4200/`) gider. Aidat hatırlatmaları her gün 06:00 UTC'de (`Jobs__DuesReminderCron`) çalışan `billing-dues-reminders` işiyle gönderilir; iş panelinden elle de tetiklenebilir. Hatırlatmalar varsayılan olarak **kapalıdır**; açmak için `Billing__DuesReminders__Enabled=true` ve `Billing__DuesReminders__StartsOn=YYYY-MM-DD` (son ödeme tarihi bundan önce olan dönemler asla hatırlatılmaz, ilk çalışma eski borçları toplu e-postalamaz).

Deploy bloğunda yapılacaklar (DEC-350, DEC-353): API henüz forwarded header'ları (`X-Forwarded-For`) işlemiyor; reverse proxy arkasında IP başına rate limit'ler (giriş, şifre sıfırlama) tek bir kova olur, `ForwardedHeadersOptions` sadece gerçek proxy adresine güvenecek şekilde ayarlanmalı. SMTP sağlayıcısı, gönderen adresi ve SPF/DKIM seçilmeli; Production'da SMTP ayarlıysa `Email__AppBaseUrl` https ve localhost olmayan bir adres olmalı (yoksa API açılmaz).

### Belge deposu (P2-F6)

Öğrenci belgeleri (sağlık raporu, fotoğraf) S3 uyumlu bir depoda tutulur. Yerelde ve testlerde compose'daki `storage` servisi (SeaweedFS, Apache-2.0; DEC-320) kullanılır: `127.0.0.1:58333`, bucket açılışta yoksa oluşturulur, herkese açık erişim yoktur. Production deposu Deploy bloğunda seçilecek.

Depo isteğe bağlıdır ve compose'un `storage` profilindedir; düz `docker compose up` onu başlatmaz ve `STORAGE_*` anahtarlarını istemez (DEC-326):

```bash
bash scripts/setup-env.sh                       # eski bir .env'e sadece eksik STORAGE_* anahtarlarını ekler
docker compose --profile storage up -d --wait   # PostgreSQL, Seq ve SeaweedFS
bash scripts/run-api.sh                         # depo çalışıyorsa Storage__* ayarlarını verir
```

`setup-env.sh` var olan `.env`'in değerlerini değiştirmez ve yazdırmaz. Anahtarlar olmadan SeaweedFS başlamayı reddeder (kimliksiz SeaweedFS anonim isteklere açık olurdu; mesaj `docker compose --profile storage logs storage`'da); diğer servisler, `scripts/e2e.sh` ve CI'daki `e2e` işi etkilenmez. Depo yapılandırılmamışsa API açılır, belgeler kullanılamaz ve `/health/ready` `Degraded` döner.

- API ayarları `Storage__ServiceUrl`, `Storage__AccessKey`, `Storage__SecretKey`, `Storage__Bucket` (isteğe bağlı `Storage__PublicUrl`: tarayıcı depoya başka bir adresten ulaşıyorsa imzalı bağlantıların adresi); `Storage__ServerSideEncryption` (varsayılan `true`, SSE-S3) sağlayıcıya bağlıdır: SeaweedFS ve AWS S3 destekler, desteklemeyen bir serviste `false` yapılıp şifreleme servisin kendi ayarıyla sağlanır (DEC-320); `run-api.sh` bunları, depo çalışıyorsa `.env`'den verir.
- Yüklemeler API üzerinden geçer ve içerik (magic bytes ve yapı) kontrol edilir; açma bağlantıları 5 dakikalık imzalı URL'lerdir (DEC-321 – DEC-323). Silinen belgenin dosyası commit'ten sonra outbox mesajıyla silinir; başarısız silme yeniden denenir (DEC-325).

### Demo verisi (P1-38)

```bash
bash scripts/seed-demo.sh   # migration'lardan sonra, API çalışmıyorken de olur
```

İki sentetik okul oluşturur: **Kadıköy Spor Kulübü** (`kadikoy-spor`) ve **Bornova Gençlik Spor** (`bornova-genclik`). Her okulda branşlar, 3 tesis, 5–6 antrenör, 10–12 grup (haftalık program ve aidatla), dolulukları %65–100 olan ~120–200 öğrenci (kardeşler aynı veliyi paylaşır, birkaç yetişkin, iki ayrılan öğrenci), son üç ayın yoklamaları, ödemeleri (bir kısmı eksik ve bir iptal edilmiş kayıt), ödenmemiş borçlar ve duyurular vardır.

- Hesaplar: `admin@<slug>.test` (Okul Admini), `antrenor1@<slug>.test` … (antrenörler); hepsi aynı şifreyi kullanır. Şifre `.env`'deki `DEMO_PASSWORD`'dan gelir; yoksa üretilir ve **bir kez** ekrana yazılır (repoya asla girmez).
- Her değişiklik uygulama katmanından (command handler'lar, doğrulama, domain kuralları, unit of work) geçer; geçmiş, saat o güne ayarlanarak gün gün yeniden oynatılır, böylece "geçmiş döneme aidat yok" gibi kurallar da korunur. Rastgelelik sabit bir tohumla üretilir (aynı gün aynı veri).
- Demo okullar zaten varsa hiçbir şey yapmaz (çıkış kodu 3). `Production` ortamında (ortam değişkeni verilmemişse de) çalışmaz (çıkış kodu 2); betik `DOTNET_ENVIRONMENT=Development` verir. Bağlantı dizesi `localhost`, `127.0.0.1` veya `::1` dışında bir sunucuyu gösteriyorsa da çalışmaz (çıkış kodu 4).
- Yarıda kalırsa (çıkış kodu 5) her command ayrı commit edildiği için kısmi demo verisi kalabilir. Yerel veritabanını sıfırlayıp baştan başlamak için (yerel **tüm** veriyi siler):
  ```bash
  docker compose down -v && docker compose up -d --wait && bash scripts/ef.sh database update
  bash scripts/seed-demo.sh
  ```
- Telefonlar `+90 5xx 000 nnnn` biçiminde, e-postalar ayrılmış `.test` alan adında; isimler yaygın Türkçe ad ve soyadların rastgele birleşimidir. Telefonların gerçek bir hatta denk gelmemesi sadece bu biçim kuralına dayanır (yönlendirilemez olduğu garanti değildir); uygulama demo kişilere SMS, WhatsApp veya e-posta göndermez, böyle bir entegrasyon eklenirse demo verisi hariç tutulmalıdır.

### İlk Platform Admin

`setup-env.sh`, `.env`'e `PLATFORM_ADMIN_EMAIL` ve rastgele bir `PLATFORM_ADMIN_PASSWORD` yazar ve şifreyi **bir kez** ekrana basar. `run-api.sh` bunları API'ye `PlatformAdmin__Email` / `PlatformAdmin__InitialPassword` olarak verir. API açılırken:

- Hiç Platform Admin yoksa ve ikisi de verilmişse, şifre değiştirme zorunlu bir Platform Admin oluşturur. Aynı anda açılan birden fazla örnek bir advisory lock ile sıraya girer; en fazla bir Platform Admin oluşur.
- Platform Admin zaten varsa hiçbir şey yapmaz (yapılandırmadaki e-posta farklı olsa bile).
- İkisi de verilmemişse hiçbir şey yapmaz. Sadece biri verilmişse, şifre hâlâ `change-me…` ise veya e-posta başka bir hesaba aitse, eksik/sorunlu ayarın **adını** uyarı olarak loglar ve atlar (değerler asla loglanmaz).
- E-posta geçersizse veya şifre politikaya uymuyorsa (en az 12 karakter, yaygın şifre değil) API açılmaz (fail-closed).

İlk girişte şifreyi değiştirdikten sonra `PLATFORM_ADMIN_PASSWORD` satırını `.env`'den silin.

## Testler

```bash
dotnet test --solution SporAkademi.slnx   # Docker gerekir (Testcontainers)
(cd web/admin && npm test)
(cd web/parent && npm test)        # ayrıca: npm run lint, npm run typecheck, npm run format:check
```

### Uçtan uca testler (Playwright, P1-36)

```bash
docker compose up -d --wait                          # PostgreSQL
cd web/admin && npm ci && npx playwright install     # bir kez (tarayıcılar)
npm --prefix ../parent ci                            # veli uygulaması da build edilir
cd ../.. && bash scripts/e2e.sh                       # tüm projeler
bash scripts/e2e.sh --project=chromium --project=mobile-chromium   # sadece Chromium
```

- Her çalıştırma compose PostgreSQL'inde ayrı bir `spor_akademi_e2e` veritabanını silip yeniden kurar ve migration'ları migrator rolüyle uygular; geliştirme veritabanına (`spor_akademi`) ve çalışan dev API'ye/arayüze dokunmaz. Okul, Okul Admini ve antrenörler çalıştırmaya özel üretilen şifrelerle API üzerinden oluşturulur; hiçbir şifre diske yazılmaz.
- Playwright API'yi `http://127.0.0.1:5180`'de, admin'in production build'ini `https://localhost:4300`'de (çalıştırmaya özel self-signed sertifika, `/api` proxy'si `e2e/serve.mjs`) başlatır ve sonunda kapatır. Bu iki port boş olmalı.
- Projeler: masaüstü `chromium`, `firefox`, `webkit` (Okul Admini akışları) ve telefon boyutunda `mobile-chromium` (360 px), `mobile-webkit` (antrenör yoklaması, süre loglanır); veli uygulamasında 360 px Chromium'da `parent-mobile-chromium` (davet e-postası → şifre → giriş → çocuk, yoklama, aidat → Fake sandbox ile online ödeme → PWA: manifest, service worker, çevrimdışı "Bağlantı yok"). Betik davet e-postası için compose'un Mailpit'ini (`mail` profili) kendisi başlatır ve veli uygulamasını `https://localhost:4301`'de sunar. Ziyaret edilen ana sayfalarda axe çalışır; `serious`/`critical` bulgular testi kırar.
- Rapor: `web/admin/playwright-report` (`npx playwright show-report`); hata olursa trace'ler `web/admin/test-results`'ta.
- Git worktree'den çalıştırırken `.env` ana checkout'taysa: `SPOR_AKADEMI_ENV_FILE=/yol/.env bash scripts/e2e.sh`.
- CI'da `e2e` işi aynı betiği geçici bir `.env` ile çalıştırır; zorunlu check değildir (DEC-240).

## Lisans

Lisans yoktur. Tüm hakları saklıdır.
