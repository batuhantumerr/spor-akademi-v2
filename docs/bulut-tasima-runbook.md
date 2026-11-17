# Bulut taşıma runbook'u (yerel / VPS'ten Azure'a)

P5-06. Sorumlu: sahip (Azure hesabı ve production erişimi olan tek kişi). Agent bulut hesabına erişemez; bu belge **yerelde ve CI'da kanıtlananı** ve
**gerçek ortamda ilk kez denenecek olanı** ayırır. Her adımın sonunda "Doğrulama" yazan satır, adımın tamamlandığının kanıtıdır.

Altyapı kodu ve kaynak listesi: [bulut-altyapisi.md](bulut-altyapisi.md) (P5-05, `infra/`). Yedek ve geri yükleme: [geri-yukleme-tatbikati.md](geri-yukleme-tatbikati.md).
Kararlar: DEC-650 – DEC-657.

Durum işaretleri: **[kanıtlandı]** yerelde ya da CI'da çalıştırıldı; **[denenmedi]** Azure'da denenmesi gerekir.

## 0. Önce bilinmesi gerekenler

- **Bölge ve KVKK:** Azure'da Türkiye bölgesi yok (ADR-0014, DEC-649). Gerçek veri taşımadan önce bölge ve standart sözleşme kararı (bölüm 6 o belgede) verilmiş olmalı.
- **Bütçe:** NFR-CST-01 (20 USD/ay) tutmuyor (DEC-648); staging ~27, production ~62 USD/ay. Kararı sahip verir.
- **Uygulama ayarları (DEC-655):** uygulama sırlarını **ortam değişkeninden** okur; Container Apps bunları Key Vault'tan doldurur. Uygulamada Key Vault SDK'sı yok ve gerekmiyor.
- **JWT anahtarı hex olmalı:** `Jwt__SigningKey` en az 32 bayt, **onaltılık (hex) yazımla** (64 karakter). `openssl rand -hex 32` üretir. Base64 ya da düz metin API'yi başlatmaz.
- **Şifre ve anahtarlar terminal geçmişine yazılmaz.** Aşağıdaki komutlardaki `<...>` değerlerini kendi kabuğunuzda değişken olarak tanımlayın; `.env` dosyası kullanılmaz ve okunmaz.

## 1. Hazırlık (yerelde)

1. `main` güncel ve CI yeşil olmalı; CI'ın GHCR'ye bastığı imajlar `ghcr.io/<sahip>/spor-akademi-v2-{api,migrator,admin,parent}:<commit SHA>` adresindedir. Taşımada kullanılacak commit SHA'sını not edin: `IMAGE_TAG`.
2. `az login` yapın (Azure CLI), doğru aboneliği seçin: `az account set --subscription <abonelik>`.
3. `tofu` (OpenTofu) kurulu olmalı (sürüm: `.github/workflows/ci.yml` içindeki sabit sürüm).
4. [bulut-altyapisi.md bölüm 10](bulut-altyapisi.md) kontrol listesindeki 1 – 5. maddeler (kararlar, abonelik yetkisi, kaynak sağlayıcıları, uzak state, GHCR görünürlüğü) tamam olmalı.

Doğrulama: `az account show` doğru aboneliği, `tofu version` beklenen sürümü gösterir.

## 2. İlk `apply`: yalnız altyapı (`deploy_apps=false`) [denenmedi]

```
cd infra/envs/staging            # önce staging, sonra production
export TF_VAR_subscription_id=<abonelik kimliği>
tofu init
tofu plan  -var image_tag=$IMAGE_TAG      # deploy_apps varsayılan false
tofu apply -var image_tag=$IMAGE_TAG
```

Bu aşama kimlik, Key Vault, ağ, PostgreSQL, depolama hesapları ve Container Apps ortamını kurar; uygulama kapsayıcılarını **kurmaz**. PostgreSQL sunucusu `pg_stat_statements`
parametresi için bir kez yeniden başlar. Başarısız olursa: rol ataması yayılımı birkaç dakika sürebilir, `apply`'ı yeniden çalıştırın.

Not edilecek çıktılar: `tofu output` (Key Vault adı, PostgreSQL FQDN, belge depolama hesabı ve konteyneri, kullanıcı atamalı kimliğin adı).

Doğrulama: `tofu plan` ikinci çalıştırmada "No changes" der; portalda kaynak grubu `rg-spor-<ortam>` içinde Key Vault, PostgreSQL, iki Storage hesabı (yalnız `enable_rabbitmq` ile ikincisi) ve Container Apps ortamı görünür.

## 3. Veritabanı rolleri ve Key Vault sırları [denenmedi]

1. PostgreSQL'e **VNet içinden** bağlanın (sunucunun genel uç noktası yoktur): Cloud Shell'in VNet entegrasyonu ya da `az containerapp job` ile geçici bir `postgres:18` kapsayıcısı. Yönetici (`sa_admin`) parolası Key Vault'ta `postgres-admin-password` sırrındadır.
2. [docker/postgres/initdb/10-create-roles.sh](../docker/postgres/initdb/10-create-roles.sh) içindeki SQL mantığını yönetici rolle çalıştırın: `spor_akademi_migrator`, `spor_akademi_app` rolleri, veritabanı sahipliği, `REVOKE`/`GRANT` ve `ALTER DEFAULT PRIVILEGES`. Parolaları rastgele üretin (`openssl rand -base64 24`).
3. Yedek rolü (`spor_akademi_backup_dump`, `BYPASSRLS`) için [create-backup-role.sql](../scripts/backup/create-backup-role.sql) denenir. **Açık risk:** yönetilen sunucuda yönetici rolün `BYPASSRLS` verip veremediği doğrulanmadı (bulut-altyapisi.md bölüm 8). Veremezse mantıksal yedek bu rolsüz alınamaz; o zaman kararı ADR-0011'de güncelleyin.
4. `CREATE EXTENSION pg_stat_statements;` (veritabanında, yönetici rolle).
5. Key Vault sırlarını yazın (yazan kullanıcıda `Key Vault Secrets Officer` rolü olmalı; `apply` eden kullanıcıya otomatik verilir). Adlar uygulamanın okuduğu ortam değişkenleriyle şöyle eşleşir (DEC-655):

   | Key Vault sırrı | Ortam değişkeni (uygulama kapsayıcısı) | Biçim |
   | --- | --- | --- |
   | `conn-app` | `ConnectionStrings__App` | `Host=<postgres_fqdn>;Database=spor_akademi;Username=spor_akademi_app;Password=...;SSL Mode=Require` |
   | `conn-migrator` | `ConnectionStrings__Migrator` (yalnız migrator işi) | Aynısı, `spor_akademi_migrator` rolüyle |
   | `jwt-signing-key` | `Jwt__SigningKey` | **64 karakter hex** (`openssl rand -hex 32`) |
   | `conn-cache` | `ConnectionStrings__Cache` | Yalnız `enable_redis` ile: `<host>:6380,password=...,ssl=True` |

   ```
   az keyvault secret set --vault-name <kv-adı> --name jwt-signing-key --value "$(openssl rand -hex 32)" --output none
   ```

   Parolaları bu komuta elle yazarken kabuk geçmişine girmemesi için önce değişkene alın (`read -rs CONN_APP`), sonra `--value "$CONN_APP"` verin.

Doğrulama: `az keyvault secret list --vault-name <kv-adı> --query "[].name"` üç sırrı listeler; sır değerleri komut çıktısına yazılmaz.

## 4. Uygulama ayarları: `extra_env` ile eklenecekler [denenmedi]

`infra/modules/apps` bugün yalnız şunları verir: `ConnectionStrings__App`, `Jwt__SigningKey`, (`ConnectionStrings__Cache`), `AllowedHosts`, `ForwardedHeaders__KnownNetworks__0`,
`OpenTelemetry__Otlp__Endpoint`. Aşağıdakiler P5-06 ile uygulamaya eklendi ve **ortam değişkeniyle verilmelidir**; `infra/envs/<ortam>/main.tf` içinde `extra_env` değişkenine yazın
(altyapı koduna dokunmadan çalışır; P5-05 birleşince bu değerlerin modüle varsayılan olarak eklenmesi ayrı bir iştir, DEC-657):

| Ortam değişkeni | Değer | Neden |
| --- | --- | --- |
| `Storage__Provider` | `AzureBlob` | Varsayılan `S3`'tür; bu olmadan API S3 ayarı arar ve belgeler `Degraded` kalır |
| `Storage__AccountName` | `tofu output documents_account_name` | Uç nokta `https://<ad>.blob.core.windows.net` olur |
| `Storage__Container` | `tofu output documents_container` (`documents`) | |
| `Storage__ManagedIdentityClientId` | Kullanıcı atamalı kimliğin **istemci kimliği**: `az identity show -g rg-spor-<ortam> -n id-spor-<ortam>-apps --query clientId -o tsv` | Kimlik kullanıcı atamalı olduğundan bu olmadan `DefaultAzureCredential` kimliği seçemez |
| `ForwardedHeaders__ForwardLimit` | `2` (bölüm 8'deki doğrulamayla) | İstek iki vekilden geçer (DEC-651) |

Hesap anahtarı ya da bağlantı dizesi **verilmez**: belge hesabında paylaşılan anahtarlar kapalıdır (P5-05); API yönetilen kimlikle (Entra ID) Blob'a yazar ve indirme bağlantılarını
**kullanıcı delegasyon anahtarı** (user delegation SAS) ile imzalar. Kimlikte `Storage Blob Data Contributor` rolü P5-05'te atanır; delegasyon anahtarı bu rolün
`Microsoft.Storage/storageAccounts/blobServices/generateUserDelegationKey/action` izniyle alınır.

Doğrulama (uygulama ayağa kalktıktan sonra): `/health/ready` yanıtı `Healthy` (bölüm 8 ve 10).

## 5. İkinci `apply` ve migration işi [kanıtlandı: imaj ve ortam değişkeni; denenmedi: Azure işi]

1. `deploy_apps=true` ile `tofu plan` / `tofu apply`: `api`, `admin`, `parent`, `gateway` ve `migrator` işi kurulur.
2. **Migration'ları çalıştırın, API'den önce ve her yeni sürümde:**

   ```
   az containerapp job start -n migrator -g rg-spor-<ortam>
   az containerapp job execution list -n migrator -g rg-spor-<ortam> -o table
   ```

   Migrator imajı kendi kendine yeten bir `efbundle` içerir (ADR-0011); bağlantıyı **`ConnectionStrings__Migrator`** ortam değişkeninden alır (DEC-653), kabuk gerektirmez.
   Bu yol yerelde boş bir PostgreSQL'e karşı ve CI'ın `images` işinde her PR'da çalışır. Bundle idempotenttir: uygulanacak migration yoksa "already up to date" der.
3. API kopyaları migration'dan önce başlamışsa `/health/ready` yalnız bağlantıyı sorar, şemayı değil; istekler hata verir. Bu yüzden işlem sırası: önce iş, sonra `api` revizyonu.

Doğrulama: işin son çalıştırması `Succeeded`; günlükte `Done.`; sonra bölüm 10 listesi.

## 6. Veri taşıma: PostgreSQL

Kaynak: yerel ya da VPS PostgreSQL. Hedef: Azure PostgreSQL Flexible Server (boş veritabanı, roller bölüm 3'te kuruldu, **migration işi henüz çalıştırılmamış**).

1. **Kaynakta bakım penceresi:** API'yi durdurun (`docker compose stop api`), böylece yazma olmaz. Kullanıcılara duyurun.
2. **Yedek rolüyle mantıksal yedek alın** (P4-09; `.pgpass` ya da `PGPASSFILE` kullanın, parola komut satırına yazılmaz):

   ```
   PGHOST=<kaynak> bash scripts/backup/backup-logical.sh ./yedek
   ```

   Çıktı: `spor_akademi-<zaman>.dump` ve `.sha256` dosyası; yedek `pg_restore --list` ile doğrulanır.
3. Hedefe yükleme **[kanıtlandı: iki yerel PostgreSQL arasında; denenmedi: Flexible Server]**. `restore-logical.sh` hedefte çalışmaz, çünkü süper kullanıcı ister ve yönetilen
   sunucunun yönetici rolü süper kullanıcı değildir. Bunun yerine **migrator rolüyle**, bakım anahtarı açık yüklenir (DEC-522: tablo sahibi migrator'ın bakım politikası yalnızca bu anahtarla bütün satırları gösterir):

   ```
   PGHOST=<postgres_fqdn> PGUSER=spor_akademi_migrator PGDATABASE=spor_akademi PGSSLMODE=require \
   PGOPTIONS="-c app.maintenance=on" \
   pg_restore --no-owner --no-privileges --exit-on-error --single-transaction ./yedek/spor_akademi-<zaman>.dump
   ```

   Bu komutu VNet içinden çalıştırın (bölüm 3, adım 1). `--no-owner --no-privileges` nesneleri migrator'a verir; uygulama rolünün yetkileri bölüm 3'te kurulan
   `ALTER DEFAULT PRIVILEGES` ile otomatik gelir. Yerelde bu yol denendi: yükleme sonrası migrator işi "No migrations were applied" dedi, uygulama rolünün tüm tablolara CRUD yetkisi
   vardı ve 29 tenant tablosunda `FORCE ROW LEVEL SECURITY` açıktı. Yönetilen sunucuda ek bir engel çıkarsa (ör. `pg_stat_statements` ya da uzantı nesneleri) hatayı kaydedin; yedekten bir PITR tatbikatı bu sunucuda ayrıca yapılmalıdır.
4. **Sonra migration işini çalıştırın** (bölüm 5, adım 2): yüklemeyle `__ef_migrations_history` zaten dolu geldiği için iş uygulanacak bir şey bulmaz; kaynak ve hedef aynı commit'in şemasındaysa bu beklenen sonuçtur. Hedef daha yeni bir sürümse iş farkı uygular.
5. Doğrulama [geri-yukleme-tatbikati.md bölüm 4, adım 6](geri-yukleme-tatbikati.md): `scripts/backup/drill-snapshot.sql` ile satır sayıları kaynakla aynı mı, `relforcerowsecurity` açık mı, uygulama rolü kendi parolasıyla bağlanıyor mu.

## 7. Veri taşıma: belge dosyaları (S3 → Blob) [denenmedi]

Belgeler PostgreSQL yedeğinin parçası **değildir**. Nesne anahtarı (S3 key) ile blob adı aynıdır; uygulama anahtarı hiçbir zaman yeniden yazmaz.

1. Kaynak depodan okuma erişimi (SeaweedFS ya da S3) ve Azure'da kendi kullanıcınıza `Storage Blob Data Contributor` rolü (belge hesabında). Hesap anahtarı kullanılmaz; kapalıdır.
2. `aws` CLI, `azcopy` ve `azcopy login` hazır olsun. Kendi kabuğunuzda değişkenleri verin (anahtarlar dosyada durmaz):

   ```
   export S3_ENDPOINT_URL=<kaynak S3 adresi>   S3_BUCKET=<bucket>
   export AWS_ACCESS_KEY_ID=...   AWS_SECRET_ACCESS_KEY=...
   export BLOB_ACCOUNT=<tofu output documents_account_name>   BLOB_CONTAINER=documents
   bash scripts/copy-documents-to-blob.sh --dry-run   # nesne sayısını gösterir, kopyalamaz
   bash scripts/copy-documents-to-blob.sh
   ```

   Betik nesneleri geçici bir dizine indirir, sonra `azcopy copy --recursive` ile aynı adlarla yükler; tekrar çalıştırmak güvenlidir. Belge sayısı küçükse (okul başına bir kaç yüz dosya) bu yeterli; çok büyükse
   `azcopy` doğrudan S3 kaynağını destekler ama S3 uyumlu özel uç noktalarda (SeaweedFS) güvenilir değildir, bu yüzden betik geçici dizin kullanır.
3. Kopya sırasında yeni yükleme olmaması için API durmuş olmalıdır (bölüm 6, adım 1). Sonradan gelen dosyalar için betik yeniden çalıştırılır.

Doğrulama: portalda konteynerdeki blob sayısı betiğin yazdığı nesne sayısıyla aynı; bir belge için uygulamadan indirme bağlantısı alıp açın (bağlantı `*.blob.core.windows.net` adresindedir ve `sig=`, `skoid=` içerir).

## 8. Sağlık ve istemci IP'si kontrolü [denenmedi]

`/health/live` ve `/health/ready` Container Apps sondalarıyla uyumludur (DEC-654):

- **Canlılık** (`/health/live`): yalnız işlem yanıt verir; bağımlılık sormaz. Veritabanı kopsa kapsayıcı yeniden başlatılmaz.
- **Hazırlık** (`/health/ready`): **veritabanı** erişilemezse `503` (kopya trafikten çıkar); belge deposu, outbox, işler ve ay kapanışı `Degraded` olabilir ama yanıt `200`'dür ve gövdesi `Degraded` yazar.
  Valkey/Redis için hazırlık denetimi yoktur (önbellek hatada devre dışı kalır); RabbitMQ denetimi eklenirse `Degraded` olmalıdır, yoksa kopyaların hepsi trafikten çıkar.
- Gateway `/health/*` yolunu dışarıya `404` döner; probe'lar API'ye iç ağdan gider.

İstemci IP'si (**gerçek veriden önce şart**, DEC-651, DEC-652): API, istemci adresini `X-Forwarded-For`'dan, **yalnız** uygulamalar alt ağından (`ForwardedHeaders__KnownNetworks__0`) gelen
vekillerden okur. İstek gateway girişi, Caddy ve `api` girişinden geçtiği için `ForwardedHeaders__ForwardLimit=2` verildi; bu sayı Azure'un tam başlık davranışı bilinmeden varsayımdır. Staging'de doğrulayın:

1. Kendi bilgisayarınızdan ağ geçidine ardışık 130 anonim istek gönderin: `for i in $(seq 1 130); do curl -s -o /dev/null -w "%{http_code}\n" https://<parent_host>/api/v1/sports; done | sort | uniq -c` — son istekler `429` almalı (anonim sınır dakikada 120).
2. **Hemen ardından başka bir ağdan** (telefon, mobil veri) tek bir istek gönderin: `401` gelmelidir. `429` gelirse bütün istemciler tek adres gibi sayılıyor demektir: `ForwardLimit`'i artırın (3) ve yeniden deneyin; bu sınır ayar değişikliğidir, kod değişikliği değildir.
3. Başlığa sahte adres ekleyin (`curl -H "X-Forwarded-For: 203.0.113.9" ...`): sınırın sayacı bu sahte adrese göre değil, gerçek adresinize göre işlemeye devam etmelidir.

## 9. DNS ve kesim (cutover) [denenmedi]

1. Önce **staging**'de bütün bu runbook'u uygulayın; production'a yalnızca staging temiz geçince geçin.
2. TTL'i kesimden en az 24 saat önce düşürün (300 sn).
3. Kesim penceresi: eski sistemde API'yi durdurun, bölüm 6 ve 7'yi (son yedek ve belgeler) çalıştırın, migration işini çalıştırın, bölüm 10 listesini uygulayın.
4. DNS: `parent_host` için `CNAME` → `gateway_fqdn`; `asuid` TXT → `custom_domain_verification_id`; Container Apps'te yönetilen sertifikalı özel alan adı (bulut-altyapisi.md bölüm 10, adım 10). `AllowedHosts`, `Email__AppBaseUrl` ve ödeme `CallbackUrl` ayarlarını gerçek adreslerle verin.
5. Admin ve veli adresleri için aynı kontrol listesi; ödeme sağlayıcısının webhook adresini yeni adrese çevirin.
6. Eski sistemi **en az bir hafta** salt okunur ya da kapalı ama silinmemiş tutun (geri dönüş için).

## 10. Doğrulama kontrol listesi (kesimden hemen sonra)

- [ ] `https://<alan-adı>/healthz` yanıt veriyor; `/health/ready` dışarıdan `404` (gateway kapatıyor), API probe'ları sağlıklı (portalda kopya `Running`).
- [ ] API günlüğünde `/health/ready` sonucu `Healthy`: Container Apps günlüklerinde `storage` için `Degraded` yok.
- [ ] Bir okul admini giriş yapar; öğrenci listesi kaynakla aynı sayıda.
- [ ] Bir belge indirilir (bağlantı `blob.core.windows.net` ve kısa ömürlü); bir fotoğraf küçük resmi admin ekranında görünür (CSP `img-src` = `STORAGE_PUBLIC_ORIGIN`).
- [ ] Bir belge yüklenir ve silinir; Blob'da yüklenen nesne görünür, silinince kaybolur.
- [ ] Bölüm 8'deki IP doğrulaması geçti.
- [ ] Başka bir okulun kaydı `404` (RLS ve tenant); `scripts/backup/drill-snapshot.sql` çıktısı kaynakla aynı.
- [ ] Bir yedek (`backup-logical.sh`) yeni sunucudan alınabiliyor ya da platform yedeği (PITR) görünüyor.
- [ ] Alarm ve bildirim kanalı kuruldu (bulut-altyapisi.md bölüm 9; Azure Monitor karşılıkları yazılmadı).

## 11. Geri dönüş (rollback)

Kesimden **önce** (DNS değişmeden): yapılacak bir şey yok; eski sistem hâlâ canlıdır. `tofu destroy` ile staging temizlenebilir.

Kesimden **sonra**, ilk günlerde:

1. DNS'i eski adrese geri çevirin (TTL düşük olduğu için dakikalar).
2. **Veri farkı:** Azure'da kesimden sonra girilen kayıtlar eski sistemde yoktur. Eski sistem kapalıydıysa ve yazma yapılmadıysa sorun yoktur. Yazma yapıldıysa Azure'dan yeni bir yedek alıp
   (`backup-logical.sh`) eski sunucuya geri yükleyin ([geri-yukleme-tatbikati.md bölüm 4, adım 5b](geri-yukleme-tatbikati.md)); belgeleri aynı betikle ters yönde kopyalayın (bu betik yalnız S3 → Blob'dur,
   ters yön için `azcopy copy` Blob → yerel dizin, sonra `aws s3 sync` kullanılır).
3. Uygulama sürümünü geri almak gerekirse: önceki commit SHA'sıyla `tofu apply -var image_tag=<eski>`. **Migration'lar ileriye dönüktür;** şema değişmişse eski imaj çalışmayabilir. Bu yüzden her sürüm geriye uyumlu migration (önce ekle, sonra kaldır) kuralına uyar; geri dönüş şüpheliyse PITR'a (geri-yukleme-tatbikati.md) başvurun.
4. `S3` sağlayıcısına dönmek: `Storage__Provider` kaldırılır (varsayılan `S3`) ve S3 ayarları (`Storage__ServiceUrl`, `Storage__AccessKey`, `Storage__SecretKey`, `Storage__Bucket`) verilir. S3 yolu hiç değişmedi.

## 12. Bilinen eksikler

- Bu belgedeki Azure adımları hiç gerçek abonelikte çalıştırılmadı. Yerelde kanıtlananlar: Blob adaptörü (Azurite), imza ve yapılandırma testleri, migrator imajının ortam değişkeniyle çalışması, bölüm 6'nın iki yerel PostgreSQL arasındaki provası, `ForwardLimit` testleri.
- Kullanıcı delegasyonlu SAS **Azurite'te denenemez** (öykünücü yönetilen kimlik vermez). Testler paylaşılan anahtarla imzalayan, yalnız testte bulunan bir imzalayıcı kullanır; kullanıcı delegasyon imzalayıcısı sahte bir istemciyle (anahtarı bir kez alıp yeniden kullanma ve yenileme mantığı) test edilir. İlk gerçek doğrulama staging'dir (bölüm 10, belge indirme).
- `infra/` içine `Storage__*`, `ForwardedHeaders__ForwardLimit` ve istemci kimliği çıktısı henüz varsayılan olarak eklenmedi (bölüm 4: `extra_env`). P5-05 birleşince ayrı küçük bir iş.
- JWT anahtarı hex biçimi bulut-altyapisi.md'de "32 bayt rastgele" diye geçer; bu belge doğrusunu yazar (bölüm 0).
