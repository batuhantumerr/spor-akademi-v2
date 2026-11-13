# Performans bütçesi ve yük testi (P4-08)

Gereksinimler: NFR-PRF-01 (okuma p95 < 300 ms), NFR-PRF-02 (yazma p95 < 500 ms), NFR-PRF-06 (N+1 yok, filtre sütunları index'li), NFR-SCL-01 (durumsuz API, proxy arkasında iki örnek). Kararlar DEC-560 – DEC-569 ([karar günlüğü](karar-gunlugu-v2.md)).

**Sonuç:** 300 eşzamanlı kullanıcıda (Ek C'deki yoğun saat varsayımı) okuma p95 **12,9 ms**, yazma p95 **72,5 ms**, hata oranı **%0**. Bütçenin yaklaşık 20 katı (okuma) ve 7 katı (yazma) pay var. İki API örneği Caddy arkasında da aynı testi hatasız geçti (okuma p95 13,9 ms, yazma p95 77,8 ms). Ölçüm yerel makinede, sentetik veriyle yapıldı; production süresi değildir (bkz. §6).

## 1. Nasıl çalıştırılır

```bash
docker compose up -d --wait
bash scripts/perf/prepare-db.sh            # bir kez: spor_akademi_perf'i kurar ve doldurur (~21 dk)
bash scripts/perf/run-k6.sh                # 1 API örneği, 300 sanal kullanıcı, ~5,5 dk
bash scripts/perf/run-k6.sh --instances 2  # Caddy arkasında 2 örnek (NFR-SCL-01)
bash scripts/perf/run-k6.sh --checks       # oturum, SignalR, backplane ve rate limit kontrolleri
bash scripts/perf/rls-bench.sh             # RLS maliyeti (pgbench, EXPLAIN ANALYZE)
```

`.env`'de `DEMO_PASSWORD`, `JWT_SIGNING_KEY` ve `VALKEY_PASSWORD` gerekir. Sonuçlar `perf/k6/out/` altına yazılır (git'e girmez): `summary-<etiket>.md` (bu belgedeki tablolar), k6'nın tam JSON'u ve koşu sırasındaki en pahalı ve en sık sorgular (`pg-stat-<etiket>.txt`, `pg_stat_statements`).

- **Veri** (`prepare-db.sh`, DEC-562): geliştirme veritabanına dokunmaz, ayrı `spor_akademi_perf` veritabanını sıfırdan kurar. Demo seeder'ı (`scripts/seed-demo.sh`) uygulama katmanından geçerek iki demo okulu ve 12 büyük yük testi okulu üretir (üç aylık geçmiş, gün gün), her okulda 20 velinin hesabını açar (davet ve kabul). Giriş adresleri `perf/k6/out/manifest.json`'a yazılır (parola yazılmaz). Geçmişin bekleyen outbox mesajları işlenmiş sayılır, yoksa ilk koşu üç aylık bildirim birikimini boşaltır.
- **API** (`run-k6.sh`): compose'un `scale` profilindeki API imajı (`docker/api/Dockerfile`, Release, production imajıyla aynı). `ASPNETCORE_ENVIRONMENT=LoadTest`, Valkey L2 cache ve SignalR backplane açık, rate limit'ler yalnız bu ortamda yükseltilmiş (DEC-561). Her koşu API'yi yeniden başlatır (boş bellek cache'i ve sınırlayıcılar).
- **k6** (`perf/k6/`, DEC-560): `grafana/k6:2.3.0` imajı, aynı compose ağında. `load.js` karışık senaryo, `scale-checks.js` iki örnek kontrolleri.

## 2. Senaryo

| Rol | Sanal kullanıcı (zirve) | Ekranlar | Yazma |
| --- | ---: | --- | --- |
| Okul Admini | 30 | Dashboard, öğrenci listesi (rastgele sayfa), öğrenci arama (%60), öğrenci detayı, bu ayın borçları, gruplar, ödemeler, rapor (%30: yoklama, doluluk veya gelir) | Masada ödeme kaydı (%50, bu ayın açık borcundan 1 TL) |
| Antrenör | 120 | Gruplarım, bugünün antrenmanları, dünün (yoksa önceki günün) yoklama listesi, kadro (%50), duyurular | Yoklama kaydı (o antrenmanın bütün öğrencileri; 48 saatlik pencere içinde düzeltme) |
| Veli | 150 | Çocuklarım, çocuğun programı, borçları, ödemeleri (%50), yoklaması, duyuruları (%50) | — |

Her sanal kullanıcı kendi hesabıyla bir kez giriş yapar (300 giriş), sonra her ekran arasında 1–3 sn düşünür. Profil: 1 dk'da 300 kullanıcıya çıkış, 4 dk sabit, 20 sn iniş. Eşikler `load.js`'te: okumaların p95'i < 300 ms, yazmaların p95'i < 500 ms, başarısız istek < %1 (okuma, yazma ve giriş ayrı ayrı), check'ler > %99. Eşik aşılırsa k6 sıfırdan farklı çıkış kodu döner.

## 3. Ortam

| | |
| --- | --- |
| Makine | Geliştirici dizüstü/masaüstü, Windows 11, Docker Desktop (WSL2), 32 mantıksal işlemci, Docker VM'inde 15,5 GiB bellek |
| Topoloji | PostgreSQL 18.6, Valkey 9.1.3, API (Release imajı) ve k6 aynı makinede, aynı Docker ağında (ağ gecikmesi ~0) |
| API | `LoadTest` ortamı, Npgsql havuzu örnek başına en fazla 40 bağlantı, outbox ve Hangfire işleri her örnekte açık |
| Veri | 14 okul, 5 845 öğrenci (en büyük okul 487), 5 845 kayıt, 425 kullanıcı (14 Okul Admini, 131 antrenör, 280 veli), 145 055 yoklama satırı, 18 801 ödeme; veritabanı 130 MB |

Ek C §1'deki varsayımla karşılaştırma: 50 okul ve 5 000 öğrenci (en büyük 500) varsayılıyor. Öğrenci sayısı ve en büyük okul karşılandı, okul sayısı değil. Bütün sorgular okul içinde ve `tenant_id` ile başlayan index'ler üzerinden çalıştığı için (DEC-524) okul sayısı sorgu maliyetini değil, yalnız tablo boyutunu etkiler. En büyük okul ölçüldü.

## 4. Sonuçlar

### 4.1 Tek API örneği (`load-1x-b`, 2026-11-13)

46 885 istek, ortalama **142 istek/sn**, %0 hata, 7 955 senaryo turu, 300 sanal kullanıcı.

| Seri | Sayı | p50 ms | p95 ms | p99 ms | max ms |
| --- | ---: | ---: | ---: | ---: | ---: |
| **Bütün okumalar** | 43 314 | 6,5 | **12,9** | 22,1 | 358,8 |
| **Bütün yazmalar** | 3 271 | 23,5 | **72,5** | 121,5 | 280,1 |
| GET /dashboard | 579 | 23,5 | 34,5 | 56,2 | 358,8 |
| GET /dues/outstanding | 579 | 12,2 | 17,7 | 22,8 | 30,6 |
| GET /reports/attendance | 52 | 14,7 | 24,2 | 43,8 | 54,8 |
| GET /groups/:id/sessions/:key/attendance | 2 990 | 6,4 | 9,6 | 12,1 | 86,5 |
| GET /parent/children/:id/attendance | 4 266 | 8,8 | 12,3 | 15,4 | 72,6 |
| PUT /groups/:id/sessions/:key/attendance | 2 990 | 23,7 | 73,4 | 121,3 | 280,1 |
| POST /payments | 281 | 21,3 | 60,9 | 119,4 | 189,6 |
| POST /auth/login | 300 | 74,7 | 137,1 | 215,8 | 755,0 |

Uç nokta başına tam tablo koşunun `summary-*.md` dosyasında. En yavaş okuma dashboard: okulun her şeyini özetleyen 11 sorgu (cache'te 30 sn / 1 dk, DEC-314; her yazma okulun dashboard'unu geçersiz kılar, yük testinde ödeme ve yoklama yazmaları bunu sık sık yapıyor). En yavaş yazma yoklama kaydı: tek transaction'da oturum, kayıtlar, değişiklik geçmişi (`attendance_changes`) ve veli bildirimleri için outbox. Giriş bilerek yavaştır (PBKDF2, NFR-SEC-03) ve bütçe dışındadır.

İlk koşu (`load-1x`) seed'in bıraktığı ~111 000 bekleyen outbox mesajının yaklaşık 34 000'ini aynı anda işlerken yapıldı (outbox işçisi ve bildirim sorguları yükün yanında). O koşuda da okuma p95 12,1 ms, yazma p95 69,1 ms çıktı. Bu yüzden `prepare-db.sh` artık geçmişin mesajlarını işlenmiş sayıyor.

### 4.2 İki API örneği, Caddy arkasında (`load-2x`)

47 037 istek, 142,7 istek/sn, %0 hata. Okuma p50 7,5 / p95 **13,9** / p99 24,0 ms; yazma p50 24,9 / p95 **77,8** / p99 121,8 ms. Tek örnekten ~1 ms yavaş: proxy'nin ek atlaması. Hız kazancı yok, çünkü 142 istek/sn tek örneği de doyurmuyor. İkinci örnek burada kapasite için değil, durumsuzluğun kanıtı için var (§5). Kapasite sınırını bulan bir stres testi bu maddede yapılmadı (öneri: Deploy sunucusunda, production'a benzer donanımda).

### 4.3 Veritabanı: en sık ve en pahalı sorgular

`pg_stat_statements` (yalnız uygulama rolü, `load-1x-b`):

| Sorgu | Çağrı | Ortalama ms | Toplam ms | Not |
| --- | ---: | ---: | ---: | --- |
| `DISCARD ALL` (Npgsql'in havuz sıfırlaması) | 213 836 | 0,016 | 3 340 | Her bağlantı açılışında, §4.4 |
| `SELECT set_config(…), set_config(…)` (RLS bağlamı) | 213 317 | 0,006 | 1 194 | Her bağlantı açılışında, §4.4 |
| Yoklama değişiklik geçmişi `INSERT` | 20 687 | 0,088 | 1 826 | Yoklama yazmasının gereği |
| Grup okumaları | 28 891 | 0,044 | 1 280 | |
| Veli bildirimi alıcıları (outbox) | 20 968 | 0,060 | 1 251 | Her yoklama ve ödeme bildirimi |
| Borçlar için ödeme toplamları | 1 152 | 0,968 | 1 115 | Okulun dönem toplamı, 690 satır |
| Dashboard yoklama özeti | 518 | 2,100 | 1 088 | Dashboard, cache'li |

Büyük tablolarda sıralı tarama yok (`pg_stat_user_tables`: öğrenci, kayıt, yoklama ve ödeme tablolarında taramaların hepsi index'ten). Tek sıralı tarama outbox sağlık kontrolünde, bekleyen mesaj sayımı ~111 000 bekleyen mesajla (seed birikimi) yapılırken görüldü. Normal işleyişte bekleyen mesaj yok denecek kadar azdır ve kontrol yük yolunda değil.

**Düzeltilen:** veli ve Okul Admini için öğrencinin yoklama geçmişi, öğrencinin gruplarındaki her oturumun **bütün** öğrencilerinin kayıtlarını yüklüyordu (çağrı başına ~100 satır, tek öğrenci için ~25 gerekli). İlk koşuda veritabanında en çok zaman harcayan sorgu buydu (4 264 çağrı, 2,4 sn). Artık filtreli `Include` ile yalnız o öğrencinin kaydı okunuyor (`ListOfGroupsForStudentAsync`, DEC-566). İkinci koşuda sorgu ilk sıralardan çıktı.

**N+1 ve eksik index:** bulunmadı. `QueryCountTests` (aşağıda) okulu büyütünce sorgu sayısının değişmediğini 18 uç noktada gösteriyor. `pg_stat_statements`'ta öğrenci veya grup sayısıyla çağrı sayısı büyüyen bir sorgu yok. Bu yüzden migration eklenmedi.

### 4.4 Row Level Security'nin maliyeti (DEC-524, DEC-567)

`scripts/perf/rls-bench.sh`, pgbench, 8 istemci, 20 sn, en büyük okul, konteyner içinde TCP:

| Varyant | İşlem/sn | Ortalama ms |
| --- | ---: | ---: |
| Öğrenci sayfası: RLS + her sorgudan önce `set_config` (uygulamanın yolu) | 9 677 | 0,827 |
| Öğrenci sayfası: RLS, okul bağlantıda bir kez | 13 828 | 0,579 |
| Öğrenci sayfası: süper kullanıcı, RLS yok | 16 128 | 0,496 |
| Kullanıcı e-postayla, okul kapsamı: RLS + `set_config` | 14 855 | 0,539 |
| Kullanıcı e-postayla, platform kapsamı (`OR` kolu): RLS + `set_config` | 15 328 | 0,522 |
| Kullanıcı e-postayla, platform kapsamı: RLS, bir kez | 22 611 | 0,354 |
| Kullanıcı e-postayla: süper kullanıcı, RLS yok | 28 206 | 0,284 |

- **Politikanın kendisi** sorgu başına ~0,07–0,08 ms ekliyor. Planlayıcı okul koşulunu bir kez değerlendiriyor (`One-Time Filter`), sorgu `tenant_id` index'iyle çalışıyor (EXPLAIN çıktısı `rls-bench.txt`'te).
- **Platform tablolarındaki `OR` kolu** ölçülebilir bir fark yaratmıyor (okul kapsamı 0,539 ms, platform kapsamı 0,522 ms). `identity.users` sorgusu e-posta index'iyle tek satıra iniyor, `OR` yalnız o satırın filtresi.
- **`set_config` gidiş-dönüşü** sorgu başına ~0,17–0,25 ms ekliyor. Uygulamada en büyük kalem bu, çünkü EF çoğu okumada bağlantıyı sorgu başına açıp kapatıyor: yük testinde istek başına ortalama **4,6 bağlantı açılışı** (213 317 `set_config` / 46 885 istek). Yani istek başına ~1 ms; okuma p50'si 6,5 ms. Her açılışta Npgsql'in `DISCARD ALL` sıfırlaması da var (`set_config`'ten önce gönderilir).

**Karar (DEC-567):** şimdilik değiştirilmedi. Bütçenin 20 katı pay var ve en büyük maliyet istek başına ~1 ms. Değerlendirilen seçenekler:

1. **Bağlantıyı istek kapsamı boyunca açık tutmak** (ilk açılışta `Database.OpenConnection`). `set_config` istek başına bire iner. Ama EF'nin bağlantı sayacına interceptor içinden yeniden girmek gerekir, ve dış HTTP çağrısı (ödeme sağlayıcısı, e-posta) yapan istekler bağlantıyı o süre boyunca tutar (havuz tükenmesi riski).
2. **`set_config`'i sorguyla aynı komutta göndermek.** Ayrı bir sonuç kümesi döndüğü için EF'nin okuyucusunu bozar. `SET` parametre almadığı için değeri metne gömmek gerekir.
3. **`No Reset On Close=true`** ile `DISCARD ALL`'u kaldırmak. Interceptor iki ayarı her açılışta yeniden yazdığı için EF bağlantıları için güvenli. Ama aynı havuzu kullanan Hangfire bağlantıları interceptor'dan geçmez ve önceki isteğin okulunu taşır. Bu, DEC-520'deki "interceptor'dan geçmeyen bağlantı hiçbir şey görmez" güvencesini bozar. **Reddedildi.**

Ne zaman yeniden bakılır: production metriklerinde (P4-07 dashboard'u) okuma p95'i 100 ms'yi geçerse ya da veritabanı CPU'su darboğaz olursa, önce 1. seçenek ölçülerek denenir.

### 4.5 Sorgu sayısı testleri (regresyon, DEC-566)

`tests/SporAkademi.IntegrationTests/Performance/QueryCountTests.cs`: test API'sine bir EF interceptor'ı (`QueryCounter`) eklenir. Her uç nokta için okul iki öğrenci ve bir grupla, sonra sekiz öğrenci ve dört grupla ölçülür. Komut sayısı değişmemeli ve bütçeyi aşmamalı. Bağlantı açılışları da sayılır, çünkü her biri ayrıca RLS gidiş-dönüşü demek. Her ölçümden önce aynı istek bir kez ölçülmeden yapılır. Böylece küçük önbellekler (okulun saat dilimi, bayraklar, branş ve tesis) iki ölçümde de sıcak olur. Bütün olarak cache'lenen dashboard ve raporlar bunun dışındadır: onların maliyeti zaten cache kaçağıdır, büyütmenin yazmaları da onları ölçümden önce geçersiz kılar. Onlarda yalnız bayraklar ısıtılır. Ölçülen değerler bütçe olarak yazıldı:

| Uç nokta | Komut | Bağlantı açılışı |
| --- | ---: | ---: |
| GET /dashboard | 11 | 11 |
| GET /parent/children/{id}/dues | 7 | 7 |
| GET /dues/outstanding | 6 | 6 |
| GET /groups, GET /reports/* (üçü), GET /parent/children/{id}/attendance | 5 | 5 |
| GET /students/{id}, GET /payments, GET /groups/{id}/attendance, GET /groups/{id}/sessions/{key}/attendance | 4 | 4 |
| GET /groups/{id}/roster | 3 | 3 |
| GET /students, GET /me/sessions | 2 | 2 |
| GET /parent/children | 1 | 1 |
| PUT yoklama | 10 | 5 |
| POST /payments | 12 | 4 |

Bir değişiklik sayıyı artırırsa test, çalışan SQL'lerin listesiyle kırılır. Bilinçli bir artışta bütçe bu tabloyla birlikte güncellenir.

## 5. İki örnek ve durumsuzluk (NFR-SCL-01, DEC-564, DEC-565)

`docker compose --profile cache --profile scale up` iki API örneğini (`api-1`, `api-2`) ve önlerinde Caddy'yi başlatır (`perf/caddy/Caddyfile`). API istekleri round robin dağıtılır: bir istemcinin ardışık iki isteği farklı örneklere gider. API'ler `X-Forwarded-For`'a yalnız Caddy'nin sabit adresinden güvenir (`ForwardedHeaders:KnownProxies`, DEC-507). `run-k6.sh --checks` production limitleriyle şunları gözler (`scale-checks.js`):

| Konu | Gözlem | Sonuç |
| --- | --- | --- |
| Oturum | Giriş api-2'de, sonraki üç refresh sırayla api-1, api-2, api-1'de. Her biri çerezi döndürdü (rotasyon, DEC-114) | Durumsuz: refresh token veritabanında, access token JWT. Yük testinde 300 kullanıcı da girişten sonra hep diğer örnekle çalıştı, hata yok |
| SignalR, round robin | 10 denemede 0 bağlantı: negotiate bir örnekte, WebSocket öbüründe; bağlantı yalnız ilk örneğin belleğinde olduğu için reddedildi | Hub'a **örnek yakınlığı gerekir** |
| SignalR, hub yolunda çerez yakınlığı (`lb_policy cookie`) | 10/10 bağlantı | Seçilen yapılandırma (DEC-565) |
| Backplane (Valkey) | Bağlantı api-1'de; aynı anda biri api-2'de, biri api-1'de iki duyuru yayınlandı; ikisinin de olayı geldi | Mesajlar örnekler arasında taşınıyor (DEC-401) |
| Rate limit, istemci adresi | Proxy üzerinden 6 istek (3'ü api-1'e), sonra doğrudan api-1'e 10 istek: doğrudan olanların 7'si geçti | api-1 proxy'den gelenleri istemcinin adresine saydı: `X-Forwarded-For` uygulanıyor |
| Rate limit, örnek başına | Proxy üzerinden 30 giriş denemesi: 20'si geçti (örnek başına 10) | DEC-506 doğrulandı: N örnekte etkili sınır N katı |
| Cache | L2 Valkey ortak. L1 her örnekte ayrı, backplane'i yok | DEC-314'teki gibi: başka örnek değişikliği en geç L1 ömrü kadar sonra görür (liste 1 dk, dashboard 30 sn). Yük testinde yazma sonrası okumalar hep yazan örneğe gitmediği için bu gecikme yaşanabilir. Bilinçli kabul (DEC-314) |

**Kontrol sırasında bulunan hata (DEC-569):** IPv4 ile doğrudan gelen bağlantı ASP.NET Core'da `::ffff:a.b.c.d` (IPv4-mapped) olarak görünüyor, proxy'nin bildirdiği adres ise `a.b.c.d`. Uç nokta sınırlayıcıları (giriş/refresh, şifre sıfırlama, ödeme dönüşü; veli, rapor, davet ve hub politikalarının anonim yedekleri) adresi düz metin olarak anahtarlıyordu. Bu yüzden aynı istemci iki kovaya düşebiliyor, IPv6'da da her istek /64 içinde yeni bir adresle yeni bir kova alabiliyordu. Artık hepsi genel sınırın kuralını kullanıyor (`ClientNetwork`: IPv4-mapped → IPv4, IPv6 → /64). Test: `The_sign_in_limit_counts_a_client_once_however_its_address_is_written`.

**Deploy bloğu için (sahip):** birden çok API örneği çalıştırılırsa production Caddy'si de `/hubs/*` için yakınlık kurmalıdır (`lb_policy cookie`) ve `ConnectionStrings:Cache` her örnekte aynı Valkey'i göstermelidir (backplane). Giriş ve okul yazma sınırları örnek başına sayılır (DEC-506). Örnek sayısı kalıcı olarak 1'den fazla olursa DEC-506'daki Valkey'e taşıma maddesi devreye girer.

## 6. Sınırlar ve sonraki adımlar

- API `LoadTest` ortamında koştu (yükseltilmiş limitler, DEC-561). Production'a özgü davranışlar ölçümde yok: HSTS (yalnız Production ve HTTPS, DEC-501), proxy yapılandırılmamışsa açılış uyarısı (DEC-507), Production'daki limit doğrulaması (DEC-561) ve TLS. TLS Deploy bloğundaki Caddy'de sonlanacak; ölçümdeki proxy düz HTTP konuşuyor.
- Ölçüm tek makinede ve ağ gecikmesi olmadan yapıldı. Production'da veritabanı ayrı bir sunucuda olursa her gidiş-dönüş ağ gecikmesi kadar pahalanır. O zaman §4.4'teki bağlantı açılışı sayısı (istek başına 4,6) önemli hale gelir; ilk bakılacak yer orası.
- k6, API ve veritabanı aynı makinede CPU paylaşıyor. Sonuçlar kötü durum yönünde yanlı.
- Okul sayısı varsayımın altında (14 / 50). Sorgular okul içi olduğu için etkisi tablo boyutuyla sınırlı.
- SignalR'ın yük altındaki davranışı (çok sayıda açık bağlantı) ölçülmedi, yalnız doğruluğu kontrol edildi.
- CI'da zorunlu olmayan, elle tetiklenen `load-smoke` workflow'u var (DEC-568): bir yük testi okulu, 12 sanal kullanıcı, 1 dk, aynı eşikler. Paylaşımlı runner gürültülü olduğu için tam 300 kullanıcılı test yerelde koşulur ve bu belge her faz geçişinde güncellenir.
