# Yedekten geri yükleme tatbikatı ve runbook (P4-09)

Gereksinimler: NFR-REL-03 (günlük, şifreli, sunucu dışında, 30 gün tutulan yedek), NFR-REL-04 (RPO: MVP'de 24 saat, Faz 4'te 15 dakika, point-in-time recovery), NFR-REL-05 (RTO: 4 saat, her faz geçişinde tatbikat). Kararlar DEC-570 – DEC-577 ([karar günlüğü](karar-gunlugu-v2.md)).

Bu belge üç şey anlatır: yedeklerin nasıl alındığı, tatbikatın nasıl çalıştırıldığı ve neyi kanıtladığı, gerçek bir felakette kimin hangi komutu çalıştıracağı. Production sunucusu, şifreleme ve sunucu dışı kopya Deploy bloğundadır. Buradaki betikler yerelde ve CI'da kanıtlanmıştır.

## 1. Yedekleme modeli

İki tür yedek birlikte kullanılır (DEC-570):

| Yedek | Betik | Ne zaman | Neye yarar |
|---|---|---|---|
| Mantıksal tam yedek (`pg_dump -Fc`) | `scripts/backup/backup-logical.sh` | Günde bir ve her production migration'ından önce (NFR-REL-06) | Sürümden bağımsız, taşınabilir, tek tablo da geri alınabilir. RPO'su yedek aralığı kadardır (24 saat, MVP hedefi). |
| Fiziksel taban yedek (`pg_basebackup`) | `scripts/backup/backup-base.sh` | Günde bir | WAL arşiviyle birlikte herhangi bir ana dönüş (PITR). |
| WAL arşivi | `scripts/backup/archive-wal.sh` (`archive_command`) | Sürekli; en geç 5 dakikada bir segment (`archive_timeout = 300`) | 15 dakikalık RPO'yu sağlayan katman. |

Geri yükleme betikleri:

- `scripts/backup/restore-logical.sh <dump>`: boş bir veritabanına tek transaction'da geri yükler. Hedefte tablo varsa reddeder, böylece canlı verinin üzerine yazmaz.
- `scripts/backup/restore-pitr.sh <taban-yedek> <wal-arşivi> <pgdata> [hedef-zaman]`: boş bir veri dizinini hazırlar. PostgreSQL açılınca arşivi hedef zamana kadar uygular ve sunucuyu yazılabilir yapar (promote).

Bütün betikler `bash` ve `set -euo pipefail` ile yazıldı; Git Bash'te ve Linux'ta çalışır. Bağlantı bilgisini standart libpq değişkenlerinden (`PGHOST`, `PGPORT`, `PGUSER`, `PGDATABASE`) alır. Parola `.pgpass` dosyasından okunur, hiçbir zaman komut satırında yer almaz. `BACKUP_POST_HOOK`, bitmiş yedeği şifreleyip sunucu dışına göndermek için bir kancadır. Kancayı Deploy bloğu dolduracak.

### 1.1 Yedek rolü ve Row Level Security (DEC-571)

P4-04 (#135) tenant tablolarında `FORCE ROW LEVEL SECURITY` açıyor. Tablo sahibi olan migrator da RLS'ye tabi; bütün satırları yalnız `app.maintenance = on` iken görüyor (DEC-522). Seçenekler RLS'li şemada denendi:

| Yedeği alan | Sonuç |
|---|---|
| Migrator, `pg_dump` varsayılanı (`row_security = off`) | Hata: *query would be affected by row-level security policy*. Yedek alınamaz. |
| Migrator, `--enable-row-security`, bakım anahtarı kapalı | **Sessizce boş yedek**: tablolar yalnız başlıkla yazılır, satır yoktur. En tehlikeli sonuç budur. |
| Migrator, `--enable-row-security` ve `PGOPTIONS='-c app.maintenance=on'` | Tam yedek. Ancak yazma yetkisi olan şema sahibinin parolası yedek sunucusunda durur. Ayar unutulursa yedek boş çıkar. |
| `pg_read_all_data` üyesi, `BYPASSRLS` olmayan rol | Hata (birinci satırla aynı). |
| **`spor_akademi_backup`**: `LOGIN REPLICATION BYPASSRLS` ve `pg_read_all_data` üyeliği | Tam yedek. **Seçilen yol.** |

Seçilen rol yalnız okuyabilir. `BYPASSRLS` ona yedeğin zaten içereceğinden fazlasını açmaz. `pg_dump`'ın varsayılanı `row_security = off` olduğundan, bu rol bir gün `BYPASSRLS`'i kaybederse yedek eksik çıkmaz, hata verir. Bu yüzden betikte `--enable-row-security` kullanılmıyor. `REPLICATION` yetkisi `pg_basebackup` için gerekiyor. Rol `scripts/backup/create-backup-role.sql` ile, sunucu başına bir kez, süper kullanıcıyla oluşturulur.

DEC-522'deki "kimsede `BYPASSRLS` yok" kuralına tek ve salt okunur bir istisna getiriyor. Onay listesinde sahibe soruldu.

Geri yükleme süper kullanıcıyla (`postgres`) yapılır (DEC-572). Dump sahiplikleri ve yetkileri olduğu gibi taşır: migrator tabloların sahibi, uygulama rolü CRUD yetkili. Satırları RLS'ye takılmadan yükleyebilen tek rol süper kullanıcıdır. Roller cluster nesnesi olduğu için dump'ta yer almaz. Boş sunucuda roller `docker/postgres/initdb/10-create-roles.sh` ile, gizli anahtar deposundaki parolalarla yeniden kurulur. PITR'da ise roller, parolalar, yetkiler ve RLS sunucu kopyasıyla birlikte gelir.

### 1.2 WAL arşivi ayarları (production)

```
wal_level = replica
archive_mode = on
archive_command = 'bash /opt/spor-akademi/backup/archive-wal.sh %p %f /var/lib/postgresql-wal-archive'
archive_timeout = 300
```

- `archive_timeout = 300`: trafik az olsa da en geç 5 dakikada bir segment kapanır. Arşivdeki en büyük boşluk 5 dakika artı sunucu dışına kopya gecikmesidir. Sunucu dışı kopya da en geç 5 dakikada bir alınırsa RPO 10 dakikayı aşmaz; hedef 15 dakikadır (DEC-573).
- `archive-wal.sh` aynı adlı farklı bir dosyanın üzerine yazmaz. Böylece geri yüklenen ikinci bir sunucu eski arşivi bozamaz. Dosyayı önce geçici adla kopyalar, sonra yeniden adlandırır.
- Saklama: 30 gün (NFR-REL-03). En eski saklanan taban yedekten önceki WAL segmentleri silinebilir.

## 2. Tatbikat

```
bash scripts/backup/drill.sh
```

Gerekenler: Docker ve .NET SDK (`dotnet tool restore`). `.env` okunmaz. Bütün parolalar o çalışma için üretilir ve yazdırılmaz. Compose'daki PostgreSQL'e ve volume'una dokunulmaz. Tatbikatın konteyner ve volume'ları `spor-akademi-drill-<çalışma>-*` adını taşır ve başarı ya da hata fark etmeksizin sonunda silinir (`DRILL_KEEP=1` olursa incelemek için bırakılır). Ayarlar: `DRILL_FILLER_ROWS` (sentetik dolgu satırı, varsayılan 200 000) ve `DRILL_REPORT` (süre tablosunun yazılacağı Markdown dosyası).

Adımlar:

1. **Kaynak sunucu.** `compose.yaml`'daki imaj ve init betiği kullanılır, WAL arşivleme açılır. Migration'lar migrator rolüyle uygulanır. Demo okullar DemoSeed ile, uygulama katmanından ve uygulama rolüyle eklenir. Üstüne sentetik dolgu tablosu (`drill.filler`) eklenir. Yedek rolü oluşturulur.
2. **Yedekler** yedek rolüyle alınır: mantıksal tam yedek ve fiziksel taban yedek. Bunlar ve WAL arşivi ayrı volume'lardadır, felakette korunur.
3. **Yedekten sonraki değişiklikler.** Önce değişiklik 1 yapılır: bir okulun adı değişir, `before-target` işareti eklenir. Ardından hedef zaman not edilir. Sonra değişiklik 2 gelir: `after-target` işareti ve dolgu satırlarının yarısının "yanlışlıkla" silinmesi. WAL segmenti kapatılır ve arşivlenmesi beklenir.
4. **Felaket.** Kaynak sunucu ve veri volume'u silinir.
5. **Geri yükleme A, tam yedek.** Yeni ve boş sunucu kurulur (initdb, roller, boş veritabanı), `restore-logical.sh` çalıştırılır.
   **Geri yükleme B, PITR.** Yeni veri dizini hazırlanır (`restore-pitr.sh`), WAL arşivi hedef zamana kadar uygulanır, sunucu promote edilir.
6. **Doğrulama.** Her geri yükleme kaynakla karşılaştırılır. A, yedek anındaki durumla; B, hedef zamandaki durumla karşılaştırılır (`scripts/backup/drill-snapshot.sql`, `diff` ile; DEC-575). Karşılaştırılanlar:
   - her tablonun satır sayısı ve içerik özeti (md5),
   - uygulanmış migration listesi,
   - tablo sahipleri ve yetkileri (ACL),
   - RLS bayrakları (`ENABLE` / `FORCE`) ve politikalar,
   - şema yetkileri ve varsayılan yetkiler,
   - fonksiyonlar (`SECURITY DEFINER` dahil), trigger'lar, index'ler,
   - uygulama ve migrator rollerinin özellikleri (süper kullanıcı değil, `BYPASSRLS` yok).

   Bunların yanında uygulama rolünün parolasıyla ağ üzerinden bağlanıp tenant tablosunu okuyabildiği denenir ve bilinen kayıtlar kontrol edilir. A'da okul adı yedekteki gibidir ve işaret yoktur. B'de değişiklik 1 vardır, değişiklik 2 yoktur, silinen satırlar geri gelmiştir.

`/health/ready` tatbikatta çağrılmıyor. API'yi ayağa kaldırmak JWT anahtarı, depo ve diğer ayarları gerektiriyor. Hazırlık kontrolünün baktığı veritabanı erişimi ve migration geçmişi, bunun yerine doğrudan karşılaştırılıyor. Gerçek felakette `/health/ready` runbook'un son adımıdır (bölüm 4).

### 2.1 Ölçülen süreler (2026-11-13, yerel)

Ortam: Windows 11, Docker Desktop, `postgres:18.6-alpine`. Tatbikat iki şemada çalıştırıldı:

- `main` (RLS yok, 10 000 dolgu satırı): A ve B geri yüklemeleri kaynakla birebir eşleşti. 42 tablo ve 21 migration karşılaştırıldı.
- P4-04 (#135) yerelde birleştirilerek RLS'li şema: 42 tablo, 22 migration, 48 RLS politikası, `FORCE ROW LEVEL SECURITY` açık 24 tablo. İki geri yükleme de kaynakla birebir eşleşti. RLS bayrakları, politikalar ve `SECURITY DEFINER` fonksiyonları korundu.

RLS'li şemada iki boyutta ölçüm (saniye):

| Adım | 200 000 satır (42 MB) | 3 000 000 satır (393 MB) |
|---|---:|---:|
| Mantıksal yedek (`pg_dump -Fc`) | 1,3 | 9,5 |
| Taban yedek (`pg_basebackup`) | 2,7 | 22,5 |
| A: yeni sunucu (initdb, roller, boş veritabanı) | 4,8 | 4,6 |
| A: `pg_restore` | 1,8 | 7,1 |
| A: doğrulama | 2,6 | 7,7 |
| **A: felaketten doğrulanmış veritabanına** | **9,4** | **19,6** |
| B: veri dizinini hazırlama | 2,0 | 5,1 |
| B: WAL uygulama ve promote | 3,1 | 5,0 |
| B: doğrulama | 3,0 | 9,2 |
| **B: felaketten doğrulanmış veritabanına** | **8,4** | **19,5** |

Dosya boyutları (393 MB veritabanı): dump 122 MB, taban yedek 153 MB, WAL arşivi 896 MB. WAL arşivi, 1,5 milyon satırlık silme yüzünden büyük. Kaynak sunucunun hazırlanması (migration'lar yaklaşık 5 sn, DemoSeed 50–80 sn) yedek sürelerine dahil değildir.

### 2.2 RPO ve RTO ile karşılaştırma

- **RPO (15 dk hedef):** PITR, hedef zamandan önceki son commit'e kadar her şeyi geri getirdi, sonraki hiçbir şeyi getirmedi. Production'da veri kaybı arşivdeki boşlukla sınırlıdır: `archive_timeout` (5 dk) ve sunucu dışı kopya aralığı. Hedef, sunucu dışı kopya en geç 10 dakikada bir alındığı sürece karşılanır. Sunucu diski de kaybolursa geçerli olan yalnız sunucu dışındaki arşivdir; bu kopyanın sıklığı Deploy bloğunda ayarlanır.
- **RTO (4 sa hedef):** veritabanını geri getirmek 393 MB'ta 20 saniye sürdü. Gerçek veri yılda birkaç GB ölçeğinde bile dakikalarla ölçülür (`pg_restore` burada yaklaşık 55 MB/sn veritabanı hızıyla çalıştı). RTO'yu belirleyen veritabanı değil, insan ve altyapı adımlarıdır. Bölüm 4'teki tahmini bütçe toplam 2–2,5 saattir. Hedefin yaklaşık yarısı yedekte kalır.

## 3. Tatbikat takvimi (DEC-577)

| Ne | Kim | Ne zaman |
|---|---|---|
| `restore-drill` workflow'u (sentetik veri, iki geri yükleme) | CI (otomatik) | Her pazartesi 03:30 UTC. Ayrıca `scripts/backup/**`, `docker/postgres/**`, `compose.yaml` veya migration'lar değişince PR'da. Elle de başlatılabilir. Zorunlu check değildir. |
| `bash scripts/backup/drill.sh` ve bu belgedeki sürelerin güncellenmesi | Faz sahibi / agent | Her faz geçişinde (NFR-REL-05) |
| Production yedeğinden geçici sunucuya geri yükleme (şifre çözme dahil) | Sahip | Deploy bloğundan sonra ayda bir; sonuç bu belgeye yazılır |

## 4. Runbook: gerçek felakette

Sorumlu: sahip (production erişimi olan tek kişi). Agent sadece tatbikat yapar. Süreler tahmindir; tatbikatlarla güncellenir.

| # | Adım | Komut / kaynak | Tahmini süre |
|---|---|---|---|
| 1 | Olayı doğrula, API'yi bakım moduna al ya da durdur, kullanıcılara duyur. Kayıp anını (yanlış silme, bozulma) loglardan belirle. | Seq / log, `docker compose stop api` | 15–30 dk |
| 2 | Karar: sunucu sağlam ve hata mantıksalsa (yanlış silme) **PITR**; sunucu kaybolduysa yeni sunucu ve PITR; WAL arşivi de yoksa **tam yedek**. | Bu tablo | 5 dk |
| 3 | Gerekirse yeni sunucuyu kur (production compose, aynı PostgreSQL imajı, boş volume). | Deploy bloğu runbook'u | 30–60 dk |
| 4 | Yedeği ve WAL arşivini sunucu dışından indir ve şifresini çöz (age). | Deploy bloğu | 10–30 dk |
| 5a | PITR: sunucu durdurulmuşken boş veri dizinini hazırla, sonra PostgreSQL'i başlat. Hedef zaman kayıp anından hemen öncedir, saat dilimiyle yazılır. | `bash restore-pitr.sh <taban-yedek> <arşiv> <pgdata> "2026-11-13 10:15:00+03"` | Dakikalar |
| 5b | Tam yedek: initdb betiğiyle rolleri ve boş veritabanını kur, sonra yükle. | `PGUSER=postgres bash restore-logical.sh <dump>` | Dakikalar |
| 6 | Doğrula: tablolardaki satır sayıları beklendiği gibi mi; `public.__ef_migrations_history` uygulamanın sürümüyle uyumlu mu; `relforcerowsecurity` tenant tablolarında açık mı; uygulama rolü parolasıyla bağlanabiliyor mu. | `scripts/backup/drill-snapshot.sql` (süper kullanıcıyla) | 10–15 dk |
| 7 | API'yi başlat, `/health/ready` ve giriş kontrolü yap, bakım modunu kaldır, duyur. | `curl --max-time 10 https://…/health/ready` | 10 dk |
| 8 | PITR sonrası sunucu yeni bir timeline'da açılır. Hemen yeni taban yedek al ve WAL arşivlemenin çalıştığını kontrol et (`pg_stat_archiver`). | `bash backup-base.sh <dizin>` | 10 dk |

Toplam yaklaşık 2–2,5 saattir; RTO hedefi 4 saattir.

Dikkat:

- `restore-logical.sh` dolu bir veritabanına yüklemeyi reddeder. Canlı veritabanının yanına ayrı bir veritabanı ya da sunucu kurulur, karşılaştırılır, sonra yer değiştirilir.
- PITR bütün cluster'ı geri getirir. Tek bir okulun verisini geri almak gerekiyorsa geri yüklenen kopyadan elle ve audit kaydıyla taşınır (P4-05).
- Belge deposundaki (S3) dosyalar bu yedeklerin parçası değildir. Depo sağlayıcısının sürümleme ve yedek ayarları Deploy bloğundadır.

## 5. Production için açık maddeler (Deploy bloğu)

[Onay listesi](pazartesi-onay-listesi.md) bölüm 3 ve 11'e de yazıldı:

- Günlük `backup-logical.sh` ve `backup-base.sh` (cron ya da systemd timer). `BACKUP_POST_HOOK` ile age şifreleme, R2'ye (ya da seçilen depoya) gönderme, 30 gün saklama ve en eski taban yedekten önceki WAL'in silinmesi.
- WAL arşivinin de şifreli olarak sunucu dışına en geç 10 dakikada bir kopyalanması. Alternatif olarak pgBackRest (MIT) değerlendirilebilir: şifreleme, sıkıştırma, S3 ve saklama yönetimi tek araçta gelir. Seçim Deploy bloğunda yapılır; buradaki betikler aynı modelin en küçük hâlidir.
- Yedek rolünün parolası gizli anahtar deposunda tutulur, `.pgpass` dosyası `600` izinle yazılır. `pg_hba.conf`'ta yedek rolüne yalnız yedek sunucusundan replication izni verilir.
- Migration öncesi yedek (NFR-REL-06) deploy workflow'unda `backup-logical.sh` ile alınır.
- Aylık production geri yükleme tatbikatı (bölüm 3).
