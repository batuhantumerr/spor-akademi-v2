# Pazartesi onay listesi

Sahip yokken (2026-11-06 – 2026-11-09) agent'ın aldığı ve sahibin onayını bekleyen kararlar ve işler. Her madde için: **onayla**, **değiştir** veya **geri al**. Karar verilince ilgili DEC satırı [karar-gunlugu-v2.md](karar-gunlugu-v2.md)'de `Accepted` yapılır.

Durum: Faz 1 işleri tamamlandı (P1-40 faz geçişi ve `v0.1.0` etiketi sahipte); Faz 2'de P2-F1 – P2-F7 bitti ve ana dala girdi (PR #43 – #65). P2-F8 başlamadı, sahibin ayrıntısını bekliyor. Faz geçişleri ve etiketler sahibe aittir, işaretlenmedi.

Karar sayısı: **33 soru** (aşağıda numaralı). Her sorunun sonundaki "Öneri", cevap verilmezse sürecek varsayılandır.

## Sahip kararı (2026-11-10)

Sahip bu listedeki **bütün önerileri onayladı**. Sonuç:

- **Soru 1–3, 5–16, 18–28, 30, 32, 33:** "Öneri" satırındaki seçenek geçerli; davranış değişmedi. Bölüm 5'teki kararlar ve ADR-0010 `Accepted` yapıldı, DEC-103'ün grafik ifadesi DEC-112'ye göre düzeltildi (#71). Soru 1'deki hukuki teyit ve soru 10'daki hatırlatma başlangıç tarihi sahipte kalır (SMTP hazır olunca).
- **Soru 4:** iptal penceresine gerekçeye kişisel veri yazılmaması uyarısı eklendi (#69).
- **Soru 29:** `e2e` ruleset'e beşinci zorunlu check olarak eklendi (main'deki son çalıştırmaların hepsi yeşil).
- **Soru 31:** şifre en az 12 karakter (DEC-369, #73); kod, testler ve politikayı anan dokümanlar güncellendi. Mevcut şifreler geçerli kalır, kural yeni şifre belirlemede uygulanır.
- **Soru 17 (P2-F8):** sahip önerilen varsayılanlarla ilerlenmesini istedi (2026-11-10). P2-F8 bu varsayılanlarla yapıldı (DEC-370 – DEC-376, **Proposed**, #75); varsayılanların onayı [bölüm 7](#7-p2-f8-aidat-kuralları-onay-bekleyen-varsayılanlar)'de açık.

Aşağıdaki metin karar anındaki hâliyle kayıt olarak bırakıldı.

## 1. Önce karar verilmesi gerekenler

### Veri saklama ve KVKK
1. **Saklama süresi (DEC-360; hukuki onay gerekli).** Ayrılan öğrencinin kişisel verisi için varsayılan saklama süresi 24 ay, okulun seçebileceği aralık 6–120 ay uygun mu? Ödeme kayıtları için 10 yıl (TTK md. 82; VUK 5 yıl) doğru yasal süre mi? Süreler aydınlatma metnine (NFR-PRV-06) de yazılmalı; gerçek veri döneminden (Faz 4) önce bir hukukçuya teyit ettirilmesi önerilir. Öneri: 24 ay, 6–120 ay, ödemeler 10 yıl; hukuki teyit Faz 4'ten önce.
2. **Cinsiyet (DEC-363).** Anonimleştirmede cinsiyet istatistik için tutuluyor; ad, doğum tarihi, iletişim, acil kişi, veliler ve belgeler siliniyor. Cinsiyet de silinsin mi? Öneri: tutulsun.
3. **Veli otomatik silme (DEC-365, DEC-368).** Başka öğrencisi kalmayan veli anonimleşmede tamamen siliniyor; öğrenciden ayrılan ve başka öğrencisi olmayan veli de hemen siliniyor. Davranış değişikliği: ayrılan veli artık telefonla bulunup kardeşe bağlanamaz. Onaylıyor musunuz? Öneri: evet (veri minimizasyonu).
4. **Ödeme iptal gerekçesi (DEC-367).** Gerekçe serbest metin ve kişi adı içerebilir; ödeme kayıtları değişmez olduğu için anonimleştirme onu silmiyor. Anonimleştirmede gerekçe silinsin mi (değişmezlik kuralına istisna), yoksa arayüzde "gerekçeye kişi adı yazmayın" uyarısı mı yeterli? Öneri: uyarı yeterli, kayıt değişmez.
5. **Ödemelerin yasal süre sonu (DEC-367).** Ödemeler 10 yıl sonunda silinsin mi, anonim kalsın mı? Bugün hiç silinmiyor. Öneri: bugünkü gibi kalsın; karar gerçek veri döneminden önce kesinleşsin.
6. **Dead-letter belge silmeleri (DEC-366).** Dead-letter olan belge silmeleri elle yeniden gönderiliyor (alarm ve not var). Otomatik yeniden deneme istenir mi? Öneri: şimdilik elle.
7. **Yetişkin telefon kuralı (DEC-151).** Reşit değilken telefonsuz kaydedilen bir öğrenci 18'ine bastıktan sonra, herhangi bir bilgisi (ör. sadece adı) düzenlenmeden önce kendi telefonunun girilmesi gerekir. Kabul edilebilir mi, yoksa yetişkin telefon kuralı sadece telefon/veli değiştiğinde mi kontrol edilsin? Öneri: bugünkü davranış kalsın.

### Hesap e-postaları ve hatırlatmalar
8. **Geçici şifre (DEC-351).** Hoş geldin e-postası (şifre belirleme bağlantısı) geldiğine göre yöneticiye gösterilen geçici şifre kaldırılsın mı (sadece bağlantı)? Bugün ikisi birlikte çalışıyor; kaldırmak US-01/US-05/US-08'i ve arayüzü değiştirir. Öneri: şimdilik ikisi birlikte.
9. **Hoş geldin bağlantısı ömrü (DEC-351).** Yöneticinin yanlış yazdığı bir e-posta adresi, hoş geldin bağlantısıyla hesabı bağlantı geçerli olduğu sürece o adresin sahibine verir. Ömür 24 saate indirildi; uygun mu, daha kısa mı olsun (ör. 12 sa), yoksa hesabın ilk girişinde e-posta doğrulaması mı istensin? Öneri: 24 sa.
10. **Hatırlatma başlangıç tarihi (DEC-355).** Hatırlatmalar varsayılan kapalı; hangi tarihten itibaren (`Billing:DuesReminders:StartsOn`) açılsın? (Açma adımları "Tek seferlik işlemler"de.) Öneri: SMTP hazır olduktan sonraki ayın 1'i.
11. **Son ödeme günü (DEC-355).** Aidatın son ödeme günü ayın 10'una sabit; okul bazında ayarlanabilir mi olmalı? Öneri: şimdilik sabit 10.
12. **Hatırlatma alıcıları (DEC-356).** Hatırlatmalar bütün e-postalı velilere gidiyor (yoksa yetişkin öğrencinin kendisine, reşit olmayana asla). "Birincil iletişim velisi" seçimi istenir mi? Öneri: şimdilik herkese.

### Ödeme (P2-F1)
13. **Admin paylaşımlı ödeme bağlantısı (DEC-348).** Veli portalı (Faz 3) gelene kadar Okul Admini ödeme bağlantısını paylaşıyor; bu asgari akış yeterli mi? Öneri: evet, Faz 3'e kadar.
14. **Fazla ödeme ve iade (DEC-342, DEC-346).** İade P2-F1 kapsamı dışı; geç gelen başarının yol açtığı fazla ödeme "Fazla ödeme" olarak işaretlenir ve loglanır, iade elle yapılır; online ödeme buradan iptal edilemez. İade akışı ne zaman ve nasıl istenir? Ayrıca peşin ödemeli kaydın erken bitirilmesi (DEC-184; güvenlik listesi, Fikirler 11) için iade veya uyarı istenir mi? Öneri: elle iade bu fazda yeterli, akış Faz 3'te.
    - **Faz 3 notu (2026-11-12, P3-13):** iade akışı yapıldı; ayrıntı [bölüm 10](#10-p3-13-online-ödeme-iadesi-2026-11-12).
15. **iyzico'ya giden ödeyen verisi (DEC-345; KVKK).** iyzico alıcı ve adres alanlarını zorunlu tutuyor; şimdilik sabit sandbox yer tutucuları gidiyor, öğrenci veya veli verisi hiç gitmiyor. Canlıdan önce hangi verinin gideceği kararlaştırılmalı. Öneri: veli portalıyla birlikte karar verilsin, o zamana kadar yer tutucu.
16. **Resmi iyzico paketi yerine `HttpClient` (DEC-345).** Resmi `iyzipay-dotnet` (MIT) eklenmedi; adaptör `HttpClient` ve dokümandaki IYZWSv2 imzasını kullanıyor (tedarik zinciri yüzeyi küçük). Onaylıyor musunuz? Öneri: evet.

### P2-F8
17. **Aidat kuralları ayrıntısı (P2-F8).** Roadmap'te "kardeş indirimi, dönem ortası değişiklik" yazıyor; ayrıntı gerektiği için başlanmadı: indirim oranı ve hangi kardeşe uygulanacağı, kardeş tanımı (aynı veli mi), dönem ortası grup değişikliğinde kıst veya ücret kuralı (bugün kıst yok, DEC-017, DEC-183), geçmiş aylara etkisi. Sahip önerilen varsayılanlarla ilerlenmesini istedi; seçilen varsayılanlar [bölüm 7](#7-p2-f8-aidat-kuralları-onay-bekleyen-varsayılanlar)'de onay bekliyor.

### Arayüz ve belgeler
18. **ADR-0010 ve arayüz teması (DEC-103, DEC-112).** ADR-0010 (Tailwind v4 + Angular CDK + lucide; grafik Apache ECharts) hâlâ **Proposed**. Tutarsızlık: DEC-103 grafikleri "ApexCharts" diye yazıyor, DEC-112 ve ADR-0010 ApexCharts'ı lisans nedeniyle reddedip ECharts seçiyor (kod ECharts kullanıyor). ADR-0010 kabul edilsin ve DEC-103'ün grafik ifadesi DEC-112'ye göre düzeltilsin mi? Öneri: evet.
19. **Production belge deposu ve yerel SeaweedFS (DEC-320).** Production belge deposu ne olacak (ör. Cloudflare R2, Hetzner Object Storage, AWS S3, kendi sunucunda SeaweedFS)? Gerçek veri dönemi için KVKK yurt dışı aktarım koşulu (NFR-PRV-07) seçimi etkiler. Yerelde MinIO yerine SeaweedFS kullanılmasını (MinIO topluluk sürümünün arşivlenmesi, AGPL-3.0) onaylıyor musunuz? Öneri: yerelde SeaweedFS evet; production seçimi Deploy bloğunda, yurt içi veya AB bölgesi tercihli.
20. **Sağlık raporlarına antrenör erişimi (DEC-324).** Sağlık raporlarını yalnız Okul Admini görüyor; antrenör sadece kendi grubundaki öğrencinin fotoğrafını görüyor. Antrenörün (ör. "spora uygundur" bilgisini görmek için) sağlık raporlarına erişmesi gerekir mi? Gerekirse rapor yerine sadece "rapor var / geçerlilik tarihi" gibi bir alan önerilir. Öneri: şimdilik hayır.
21. **Belge sınırları ve geçerlilik takibi (DEC-322).** Sınırlar uygun mu (rapor 5 MB, fotoğraf 2 MB, öğrenci başına 20 rapor)? Raporun geçerlilik tarihi/son kullanım takibi istenir mi? Öneri: sınırlar uygun; takip sonraki faz adayı.
22. **Fotoğraf yönü, görüntü işleme ve virüs taraması (DEC-322).** Fotoğraflardaki EXIF silindiği için telefon fotoğrafları yan görünebilir (yön bilgisi gider). Sunucuda görüntü işleme kütüphanesi (izinli lisanslı, ör. SkiaSharp MIT) eklenip fotoğraf döndürülerek yeniden kodlansın mı? Bu ayrıca virüs taraması (ClamAV, GPL-2.0, ayrı servis; güvenlik listesi, Fikirler 12) ile birlikte değerlendirilebilir. Öneri: sonraki faz, şimdilik yan görünme kabul.

### Arka plan işleri, cache ve CI
23. **Hangfire seçimleri (DEC-330 – DEC-335).** (a) Şema migrator migration'ıyla paketin kendi script'lerinden kurulur, API'nin DDL yetkisi yok (DEC-331); (b) panel erişimi kısa ömürlü (15 dk) çerezle, panel varsayılan salt okunur (DEC-332); (c) outbox varsayılan olarak Hangfire'a geçti, `Outbox:Enabled` yerine `Outbox:Scheduler` (`Worker` eski polling'i seçer) (DEC-333); (d) bitmiş oturumların refresh token'ları her gece silinir (DEC-334). Onaylıyor musunuz? Öneri: evet.
24. **Valkey ve cache tasarımı (DEC-310 – DEC-316).** Redis yerine Valkey (lisans), kişisel veri cache'lenmez, commit sonrası otomatik invalidation, L2 isteğe bağlı, kısa L1 ömürleri. Onaylıyor musunuz? Öneri: evet.
25. **Outbox tasarımı (DEC-300 – DEC-304).** Yeni paket yok; at-least-once teslim, dead-letter, 7 gün saklama. Onaylıyor musunuz? Öneri: evet.
26. **gitleaks CI aracı (DEC-200, DEC-250).** Yeni CI aracı (MIT, SHA-256'lı ikili) ve dar izin listesi onayınızı bekliyor. Öneri: kabul.
27. **GHCR imaj adları (DEC-260).** İmajlar `spor-akademi-v2-{api,migrator,admin}` oldu; eski `spor-akademi-*` paketlerine dokunulmadı. Onaylıyor musunuz? Öneri: evet.
28. **axe-core lisansı (DEC-240).** `@axe-core/playwright` MPL-2.0 (zayıf copyleft), dev bağımlılığı, dağıtılmaz. Kabul edilmezse e2e'deki axe adımları kaldırılır. Öneri: kabul.
29. **`e2e` zorunlu check (P1-36, DEC-240).** Ruleset bugün dört check istiyor (`backend`, `frontend`, `repo-checks`, `images`); `e2e` bilerek eklenmedi. Birkaç yeşil çalıştırmadan sonra ruleset'e eklensin mi? Öneri: evet.
30. **CODEOWNERS ve code-owner incelemesi.** `.github/CODEOWNERS` eklendi (CI, `.gitleaks.toml`, `docker/`, `scripts/`, `src/`, `tests/`, `docs/adr/`, paket dosyaları), ruleset değiştirilmedi. Code-owner incelemesini zorunlu yapmak sahibin kararı; zorunlu olursa hafta sonu otonom merge'leri (DEC-098) sahip onayı olmadan ilerleyemez. Sahip PR'ları kendisi açtığında GitHub kendi onayını code-owner onayı saymaz; ruleset'te bypass ya da ikinci bir inceleyici gerekir. Öneri: şimdilik zorunlu yapma, Faz 4'te yeniden değerlendir.
31. **Şifre uzunluğu (NFR-SEC-04).** Politika 10 karakter; ASVS L1 12 önerir. 12'ye çıkarılsın mı? Öneri: 12. **Onaylandı (2026-11-10): 12 karakter, DEC-369.**

### Toplu onay ve ertelenenler
32. **Proposed kararların toplu kabulü (DEC-099 – DEC-368, ADR-0010).** Bölüm 5'teki tablodaki kararlar (yukarıda ayrıca sorulanlar dahil) toplu `Accepted` yapılsın mı, itiraz ettikleriniz ayrı mı işaretlensin? Öneri: itirazı olmayanlar toplu kabul.
33. **Ertelenen fikirler.** [Güvenlik kontrol listesinin](guvenlik-kontrol-listesi.md) "Fikirler" bölümündeki adaylardan (HTTPS/HSTS, güvenlik başlıkları NFR-SEC-13, MFA, denetim kaydı, kişisel veri envanteri, arama terimi loglaması, yükleme bellek sınırı vb.) hangileri Faz 4'e alınsın? Takip notları: admin imajındaki geçici zlib/musl yükseltmeleri (DEC-104, DEC-202) base imaj düzelince kaldırılacak; dashboard'un 24 aylık yükleme maliyeti (DEC-210) ve duyuru/yoklama arayüzü erişilebilirlik cilaları (PR #43 incelemesi). Öneri: Faz 4 planlamasında ele alınsın.

## 2. Tek seferlik işlemler (sahip yapacak)

- **GHCR paketleri (DEC-260, DEC-089).** `publish` işinin ilk başarılı push'undan sonra her paket (`spor-akademi-v2-api`, `-migrator`, `-admin`) için bir kerelik: Package settings → Change visibility → Public; ve "Manage Actions access" altında bu reponun yazma izni olduğunu kontrol edin. Eski `spor-akademi-*` paketleri eski repoya bağlı görünüyor; dokunulmadı.
- **Aidat hatırlatmalarını açmak (DEC-355).** Varsayılan kapalı. Açmak için `Billing:DuesReminders:Enabled=true` ve bir başlangıç tarihi `Billing:DuesReminders:StartsOn` birlikte gerekir (bu tarihten önce vadesi gelen dönemlere hatırlatma gitmez). SMTP ayarlı olmalı (Deploy bloğuna bakın).
- **iyzico sandbox (DEC-340, DEC-345).** Sandbox hesabı açıp anahtarları ortam değişkeniyle verin: `Payments__Iyzico__ApiKey`, `Payments__Iyzico__SecretKey`, `Payments__Iyzico__CallbackUrl`; iyzico panelinde webhook adresini ve imza ayarını (`X-IYZ-SIGNATURE-V3`) tanımlayın. `.env`'e anahtar eklenmedi; `Payments` bölümü tamamen isteğe bağlı, anahtarsız Production'da online ödeme kapalıdır ve adaptör sadece sandbox adresine bağlanır.
- **Gerçek iyzico sandbox koşusu ve detay yanıtı imzası (DEC-345).** Adaptör sahte HTTP handler'la test edildi, gerçek sandbox'a karşı çalıştırılmadı. Uçtan uca bir sandbox ödemesi deneyin; iyzico detay yanıtındaki `signature` bugün doğrulanmıyor (formül güvenilir biçimde doğrulanamadı), gerçek yanıtla doğrulanıp eklenmeli.
- **Valkey (isteğe bağlı, DEC-316).** Bir kez `bash scripts/setup-env.sh` (sadece eksik `VALKEY_*` anahtarlarını ekler) ve `docker compose --profile cache up -d --wait`. `VALKEY_PASSWORD` olmayan eski `.env` hiçbir şeyi bozmaz.
- **Mailpit (isteğe bağlı, DEC-357).** Bir kez `bash scripts/setup-env.sh` (sadece eksik `MAILPIT_*` port anahtarlarını ekler) ve `docker compose --profile mail up -d --wait`; arayüz `http://127.0.0.1:58025`.
- **Belge deposu (isteğe bağlı, DEC-326).** `.env`'e elle bir şey eklemek gerekmez. Kullanmak için `bash scripts/setup-env.sh` (sadece eksik `STORAGE_*` anahtarlarını ekler) ve `docker compose --profile storage up -d --wait`.
- **Etiketler ve faz geçişleri.** `v0.0.1` (P0-35) ve `v0.1.0` (P1-40) etiketleri ve faz geçişleri sahipte.
- **Öğrenme günlüğü.** Not taslakları `docs/ogrenme-gunlugu.md`'de.

## 3. Deploy bloğuna bırakılanlar

- **ForwardedHeaders / KnownProxies (DEC-344, DEC-353).** API reverse proxy arkasında `X-Forwarded-For`'u işlemiyor; işlenmezse IP başına rate limit'ler (giriş, şifre sıfırlama, ödeme webhook ve callback'leri) bütün istemciler için tek kova olur. `ForwardedHeadersOptions` sadece gerçek proxy adresine güvenecek şekilde ayarlanmalı (proxy IP'si tahmin edilip yapılandırılmadı).
- **nginx `/jobs` yönlendirmesi (DEC-332).** Üretim nginx'i `/jobs`'u yönlendirmiyor (şimdilik sadece yerel dev proxy). Yönlendirilirken, panel oturumları süreç belleğinde olduğu için birden çok API örneğinde yapışkan oturum veya paylaşılan oturum deposu gerekir.
- **SMTP (DEC-350).** SMTP sağlayıcısı, gönderen adresi, SPF/DKIM ve `Email__AppBaseUrl` (Production'da https ve localhost olmayan adres zorunlu; şifresiz SMTP reddedilir).
- **Production belge deposu ve SSE (DEC-320).** Sağlayıcı seçimi (soru 19) ve `Storage:ServerSideEncryption` (varsayılan `true`, SSE-S3/AES256; SeaweedFS ve AWS S3 kabul eder, başlığı reddeden servis için sağlayıcıya göre ayarlanır).
- **CSP `img-src` (güvenlik listesi, Fikirler 12).** CSP gelince fotoğraf küçük resmi doğrudan imzalı URL'den yüklendiği için depo origin'i `img-src`'ye eklenmeli. CSP ve güvenlik başlıkları (NFR-SEC-13) ile HTTPS/HSTS (NFR-SEC-02) de Deploy bloğu veya Faz 4.
- **Production'da Valkey (DEC-310, DEC-316).** Yerelde isteğe bağlı; production'da L2 olarak çalıştırılıp çalıştırılmayacağı (yoksa sadece bellek) ve parolası Deploy bloğunda karara bağlanır.
- **SignalR hub'ı Caddy'de (P3-08, DEC-399, DEC-402).** `/hubs/*` API'ye `reverse_proxy` ile yönlendirilmeli (WebSocket upgrade'i Caddy kendiliğinden taşır; nginx imajları `/hubs`'a `404` döner). Caddy erişim logunda sorgu dizesindeki `access_token` silinmeli (`log { format filter { request>uri query { delete access_token } } }`), çünkü tarayıcı WebSocket'i token'ı orada taşır. Birden çok API instance'ı çalışacaksa Valkey backplane için `ConnectionStrings__Cache` verilmeli; kullanıcı başına bağlantı sınırı instance başınadır (DEC-400).
- **Canlı ödeme (DEC-345).** iyzico canlı adresi şu an uygulamayı başlatmıyor; canlı ödeme Deploy bloğu ve sahip onayı ister.

## 4. Paketler ve lisanslar

Bu repoya eklenen her paket ve imaj tek tabloda. Dokümandaki baz listeden gelenler (EF Core, Npgsql, EFCore.NamingConventions, xUnit v3, Shouldly, NSubstitute, Testcontainers, Respawn, FakeTimeProvider, NetArchTest, Coverlet; DEC-023, DEC-061, DEC-079) MIT / Apache-2.0 / BSD lisanslıdır. Ticari lisanslı paket yoktur.

| Paket / imaj | Tür | Neden | Lisans | Alternatifler / not |
| --- | --- | --- | --- | --- |
| `Hangfire.Core`, `Hangfire.NetCore`, `Hangfire.AspNetCore` 1.8.25 | NuGet | P2-F2 arka plan işleri ve iş paneli; doküman Hangfire'ı adıyla istiyor (DEC-330). Hangfire Pro kullanılmıyor | LGPL-3.0 (çoklu lisans; değiştirilmeden, NuGet ile dinamik bağlama) | Quartz.NET (Apache-2.0, panel yok), kendi `BackgroundService` döngüleri |
| `Hangfire.PostgreSql` 1.21.1 (geçişli `Dapper` 2.0.123, `Dapper.AOT` 1.0.48) | NuGet | Hangfire'ın PostgreSQL deposu, `hangfire` şeması (DEC-331) | LGPL-3.0 (Dapper'lar Apache-2.0) | `Hangfire.Pro.Redis` (ticari, yasak), Redis |
| `Newtonsoft.Json` 13.0.4 (merkezi geçişli sabitleme) | NuGet | `Hangfire.Core`'un istediği 11.0.1 GHSA-5crp-9r3c-p9vr açığını taşıyor | MIT | — |
| `Microsoft.Extensions.Caching.Hybrid` 10.11.0 | NuGet | P2-F7: L1 + L2 cache, etiketle invalidation, stampede koruması (DEC-310) | MIT | `IMemoryCache` + `IDistributedCache` elle, FusionCache (MIT) |
| `Microsoft.Extensions.Caching.StackExchangeRedis` 10.0.14 | NuGet | HybridCache L2'si için Redis protokolüyle `IDistributedCache` (Valkey ile çalışır) | MIT | Kendi `IDistributedCache` uygulaması |
| `StackExchange.Redis` 2.13.17 (+ `Pipelines.Sockets.Unofficial`, `System.IO.Hashing`) | NuGet | Redis/Valkey istemcisi; doğrudan referans `AbortOnConnectFail`/`BacklogPolicy` için. 3.x değil (üstteki paket 2.x'e karşı derlenmiş) | MIT | NRedisStack (MIT), Valkey GLIDE (Apache-2.0, .NET henüz olgun değil) |
| `MailKit` 4.18.2 (geçişli `MimeKit` 4.18.2, `BouncyCastle.Cryptography` 2.7.0, `System.Security.Cryptography.Pkcs`, `System.Formats.Asn1` 10.0.13) | NuGet | P2-F4 SMTP ile e-posta (DEC-350); sadece `Infrastructure.Notifications`'ta | MIT (geçişliler de MIT) | `System.Net.Mail.SmtpClient`, sağlayıcı SDK'ları, FluentEmail |
| `AWSSDK.S3` 4.0.104.8 (→ `AWSSDK.Core` 4.0.102.14) | NuGet | P2-F6 belge deposu istemcisi; `IDocumentStorage` arkasında (DEC-320) | Apache-2.0 | Minio .NET SDK (Apache-2.0), FluentStorage (MIT), elle SigV4 |
| `@playwright/test` 1.64.1 (+ `playwright-core` 1.64.1 `overrides`) | npm (dev) | P1-36 uçtan uca testler, NFR-CMP-01 (Chromium/Firefox/WebKit) (DEC-240) | Apache-2.0 | Cypress (WebKit yok), WebdriverIO |
| `@axe-core/playwright` 4.14.0 (→ `axe-core` 4.14.0) | npm (dev) | NFR-USA-02: e2e'de otomatik axe kontrolü; dağıtılmaz (DEC-240) | MPL-2.0 (zayıf copyleft) | Pa11y, Lighthouse CI; reddedilirse axe adımları kaldırılır |
| Apache ECharts + ngx-echarts | npm | Yönetici paneli grafikleri (DEC-112) | Apache-2.0 / MIT | ApexCharts (çift lisanslı, reddedildi) |
| iyzico için paket yok | — | Resmi `iyzipay-dotnet` (MIT) eklenmedi; `HttpClient` kullanılıyor (DEC-345) | — | — |
| `chrislusf/seaweedfs:4.51` (compose `storage` servisi ve Testcontainers) | imaj | P2-F6 yerel/CI S3 uyumlu belge deposu (DEC-320); production deposu Deploy bloğunda | Apache-2.0 | Digest ile sabit; yayın 2026-10-25 |
| `valkey/valkey:9.1.3-alpine` | imaj | P2-F7 L2 cache sunucusu: yerel compose ve Testcontainers (DEC-310, DEC-316). Redis ≥ 7.4 izinli lisanslı değil (RSAL/SSPL/AGPL) | BSD-3-Clause | Tag + digest |
| `axllent/mailpit:v1.32.0` | imaj | P2-F4 yerel e-posta yakalayıcı: `mail` profili (isteğe bağlı) ve Testcontainers SMTP testi (DEC-357); hiçbir şeyi dışarı iletmez | MIT | Tag + digest |
| gitleaks `v8.30.1` | CI aracı | NFR-SEC-09: repo geçmişinde gizli bilgi taraması, `repo-checks` işinde (DEC-200) | MIT | Resmi sürüm ikilisi + SHA-256 (`ci.yml`) |
| `e2e` CI işi (Playwright tarayıcıları `npx playwright install --with-deps`) | CI işi | P1-36: kritik akışlar gerçek API + PostgreSQL'e karşı, beş proje (DEC-240) | Apache-2.0 | Tarayıcılar Playwright sürümüyle sabit; action'lar SHA'lı |

## 5. Önerilen (Proposed) kararlar

[karar-gunlugu-v2.md](karar-gunlugu-v2.md)'deki bütün `Proposed` kararlar (138 DEC) ve ADR-0010, alanlara göre. Özetler günlükteki ilk cümleden kısaltılmıştır; tam metin ve gerekçe günlüktedir. DEC-097 ve DEC-098 zaten `Accepted`.

### ADR

| ID | Özet |
| --- | --- |
| ADR-0010 | Admin arayüzü UI kütüphanesi: Tailwind CSS v4 + Angular CDK + lucide, grafikler Apache ECharts ([adr/0010-ui-kutuphanesi.md](adr/0010-ui-kutuphanesi.md)). |

#### Altyapı, CI ve imajlar

| ID | Özet |
| --- | --- |
| DEC-099 | Docker Compose proje adı `spor-akademi-v2` (eski projenin `spor-akademi_postgres-data` volume'u ile çakışmasın diye). |
| DEC-100 | Uygulama rolünün migration geçmiş tablosuna erişimi, `public` şemasında `USAGE` yetkisi kaldırılarak engellenir (init script `REVOKE ALL ON SCHEMA public FROM PUBLIC`). |
| DEC-101 | Trivy `v0.76.1` (doküman sürümü); `v0.76.2` 2026-11-05'te çıktığı için 7 günlük cooldown'a takılıyor. |
| DEC-102 | `IDE0005` (gereksiz using) build'de hata olarak zorlanmıyor: `GenerateDocumentationFile` gerektiriyor; bunun yerine CI'daki `dotnet format --verify-no-changes`… |
| DEC-104 | Admin imajında geçici `apk upgrade --no-cache zlib` (CVE-2026-85091, HIGH). |
| DEC-200 | CI `repo-checks` işinde gitleaks ile gizli bilgi taraması: MIT lisanslı gitleaks `v8.30.1` (2026-03-21; 7 günlük bekleme süresini geçmiş) resmi sürüm ikilisi SHA-256 ile… |
| DEC-201 | Log gizliliği testi için Serilog yapılandırmasına `ReadFrom.Services` eklendi: DI'a `ILogEventSink` olarak kaydedilen sink'ler de tüm olayları alır (production'da… |
| DEC-202 | Admin imajında geçici `musl`/`musl-utils` >= 1.2.6-r3 yükseltmesi (CVE-2026-107891, HIGH); DEC-104 ile aynı kural: sabit sürüm bulunamazsa build hata verir, base imaj… |
| DEC-250 | gitleaks izin listesi (DEC-200'ü daraltır): yol glob'u (`^tests/`, `*.spec.ts`) ve repo genelinde geçerli değer kalıpları kaldırıldı. |
| DEC-260 | GHCR imaj adları `spor-akademi-v2-{api,migrator,admin}` olarak değişti. `publish` işi ilk günden beri `denied: permission_denied: write_package` ile kırılıyordu; en… |

#### Platform, kimlik ve çok kiracılık

| ID | Özet |
| --- | --- |
| DEC-105 | `User` global tenant filter'a tabi değildir (Platform Admin'in okulu yok; giriş e-postayla okullar arası arama gerektirir). |
| DEC-106 | Domain event işleyici arayüzünün adı `IDomainEventConsumer<T>`: CA1711 `EventHandler` sonekini yasaklıyor ve analyzer kapatılmıyor. |
| DEC-107 | JWT süre doğrulaması `TimeProvider` üzerinden yapılır (`LifetimeValidator`); böylece 15 dk / 7 gün / 30 gün kuralları testte saat ilerletilerek doğrulanır. |
| DEC-108 | Tenant yazma koruması: token'da okul varsa sadece o okulun satırları eklenir/değişir/silinir, bir satırın tenant'ı asla değişmez; ihlal `TenantIsolationException` (500,… |
| DEC-109 | Çerçevenin ürettiği çıplak yanıtlar da kodlu ProblemDetails döner. |
| DEC-110 | Token tutarlılık kuralı: `PlatformAdmin` token'ında `tenant_id` olmaz, diğer rollerde geçerli bir `tenant_id` zorunludur; aksi halde kimlik doğrulama başarısız (401). |
| DEC-111 | 1 MB gövde sınırı Kestrel ayarına ek olarak `Content-Length` kontrol eden bir middleware ile de uygulanır (sunucudan bağımsız ve test edilebilir). |
| DEC-113 | `IKeepChangesOnFailure` ile işaretli command'lar, beklenen bir hata döndürseler de transaction'ı commit eder (`IUnitOfWork.ExecuteInTransactionAsync(...,… |
| DEC-114 | Reuse detection sadece rotate edilmiş (`ReplacedByTokenId` dolu) bir token tekrar gönderilince tetiklenir ve kullanıcının tüm token'larını iptal eder. |
| DEC-115 | Kullanıcı sayımını önlemek için bilinmeyen e-posta, yanlış şifre ve biçimsiz e-posta aynı `Auth.InvalidCredentials`'ı alır; bilinmeyen e-postada sahte bir hash… |
| DEC-116 | Access token'ı imzalayan `JwtAccessTokenIssuer`, `JwtOptions` ve bearer doğrulamasıyla birlikte `Api/Authentication`'dadır (imza ve doğrulama tek yerde; Infrastructure'a… |
| DEC-117 | Yeni hata kodu eklenmedi: `X-Requested-With` eksikse `400 Validation.Failed` + `errors["X-Requested-With"] = ["Field.Required"]`; giriş rate limit'i `429` ProblemDetails… |
| DEC-118 | Admin şifre sıfırlama: Okul Admini sadece kendi okulunun antrenörlerini (`POST /coaches/{id}/reset-password`), Platform Admin sadece rotadaki okulun Okul Adminlerini… |
| DEC-119 | `identity.users.tenant_id`, `tenancy.schools.id`'ye `RESTRICT` FK ile bağlıdır (şemalar arası FK). `identity.refresh_tokens` kendi `xmin` concurrency token'ına sahiptir:… |
| DEC-120 | Yeni hata kodu `Auth.PasswordUnchanged` (Validation, alan `newPassword`): yeni şifre mevcut şifreyle aynıysa şifre değiştirme reddedilir. |
| DEC-121 | Güvenlik incelemesi sonrası: şifre değiştirmede yanlış mevcut şifre, girişteki 15 dk'da 5 deneme kilidine sayılır (`ChangePasswordCommand` da `IKeepChangesOnFailure`);… |
| DEC-122 | Okul oluşturma (US-01): Tenancy önce slug'ı kontrol edip okulu ekler, sonra `IIdentityModule.CreateSchoolAdminAsync` ile aynı transaction'da ilk Okul Adminini açar;… |
| DEC-123 | İlk Platform Admin seed'i uygulama açılışında çalışan bir hosted service'tir: Platform Admin yoksa ve `PlatformAdmin:Email` + `PlatformAdmin:InitialPassword` verilmişse,… |
| DEC-124 | Antrenörler (US-08): e-posta sadece oluştururken verilir (giriş kimliğidir), `PUT` ad ve telefonu `version` ile günceller; boş telefon "telefon yok" demektir. |
| DEC-125 | Groups için okuma sözleşmesi `ICoachDirectory` (`FindAsync`, `GetManyAsync`), Infrastructure'da `CoachDirectory` olarak uygulanır: User tenant filtresine tabi… |
| DEC-126 | Pasif bir kullanıcının profili düzenlenemez: `PUT /coaches/{id}` pasif antrenör için `422 User.AlreadyInactive` döner (yeni kod eklenmedi; "bu kullanıcı zaten pasif"… |
| DEC-130 | `Facility.NameTaken` (409) hata koduna eklendi: tesis adları da okul bazında büyük-küçük harf duyarsız benzersiz (US-07, branşla aynı kural). |
| DEC-131 | Branş ve tesis silme kontrolü Groups modülünün `IGroupUsage` sözleşmesi üzerinden yapılır; Groups (P1-18) gelene kadar yer tutucu "kullanımda değil" der. |
| DEC-132 | Command'lar sadece id döner; endpoint ardından query ile güncel DTO'yu (yeni `version` dahil) okur. |
| DEC-133 | Benzersizlik ihlalleri (23505) kısıt adına göre beklenen hataya eşlenir (`School.SlugTaken`, `Sport.NameTaken`, `Facility.NameTaken`, `User.EmailTaken`): handler… |
| DEC-134 | Sürümlü güncellemeler her zaman `xmin` koşullu UPDATE çalıştırır (değişiklik olmasa bile); eksik `version` 400 `Field.Required`. |

#### Arayüz

| ID | Özet |
| --- | --- |
| DEC-103 | Admin arayüzü teması sahibin başka projesindeki görsel dilden (slate nötrler, kart/tablo/sidebar kalıpları) uyarlanır, ana renk mavi; grafikler ApexCharts. |
| DEC-112 | Grafik kütüphanesi Apache ECharts + ngx-echarts (Apache-2.0/MIT). |

#### Gruplar ve program

| ID | Özet |
| --- | --- |
| DEC-140 | Grubun program saatleri ayrı tablo yerine `groups.groups.schedule_slots` jsonb sütununda, EF Core 10 complex collection olarak tutulur (`ScheduleSlot` ve `TimeRange`… |
| DEC-141 | Yeni hata kodu `Group.NameTaken` (409): grup adları okul bazında, Türkçe kurallarıyla büyük-küçük harf duyarsız benzersiz (branş ve tesisle aynı kural,… |
| DEC-142 | Grup oluştururken ve düzenlerken antrenör isteğe bağlıdır (`coachId` null olabilir; okulun henüz antrenörü olmayabilir, pasif antrenörün grupları zaten boş kalır). |
| DEC-143 | Antrenör çakışması (`Schedule.CoachUnavailable`, `groupName`) program değiştirilirken ve grubun antrenörü veya tesisi değiştirilirken kontrol edilir; tesis uyarısı… |
| DEC-144 | Arşivleme tarihi `archivedOn` olarak saklanır: okulun saat dilimindeki bugün (`ITenancyModule.TodayAsync`), `GroupArchived` event'iyle aktif kayıtlar bu tarihle sonlanır… |
| DEC-145 | `GET /groups/{id}/sessions` `from` ve `to` zorunlu, iki uç dahil, en fazla 62 gün (aşarsa `400`, `errors.to`). `/groups/{id}/sessions`, `/me/groups` ve `/me/sessions`… |
| DEC-146 | DEC-133'ün devamı: gruplardan branş ve tesise giden bileşik FK'ler `RESTRICT`; silmede oluşan ihlal (PostgreSQL `23001`, ve `23503`) sadece silinen satır o türdense… |
| DEC-147 | `UserDeactivated` işleyicisi (Groups) antrenörün gruplarındaki `CoachId`'yi boşaltır; tenant filtresine dayanır, yani pasifleştirmeyi yapan Okul Admininin isteğinde… |
| DEC-148 | Antrenör müsaitlik kontrolü antrenör başına serileştirilir: program değişikliği (grubun antrenörü varsa) ve antrenör değiştiren düzenleme, çakışma kontrolünden önce unit… |

#### Öğrenciler ve kayıt

| ID | Özet |
| --- | --- |
| DEC-150 | Öğrenci araması `students.search_key` sütununda (`TextNormalization.SearchKey("ad soyad")`: küçük harf, Türkçe harfler ASCII'ye katlanmış) alt dize eşleşmesiyle yapılır;… |
| DEC-151 | Reşitlik: yaş (ay, gün) karşılaştırmasıyla hesaplanır; 18. doğum günü yetişkin sayılır; 29 Şubat doğumlular artık yıl olmayan yıllarda 1 Mart'ta reşit olur. "Bugün"… |
| DEC-152 | Veli bağlantısı girdisi ya `guardianId` (okulun mevcut velisi) ya `newGuardian` (yeni veli bilgisi) taşır, ikisi birden/hiçbiri `Field.Invalid`… |
| DEC-153 | Yeni hata kodu eklenmedi. `Student.EmergencyContactRequired` (Validation) diğer alan kodları gibi `400 Validation.Failed` içinde `errors.emergencyContact` altında döner… |
| DEC-154 | `GET /guardians?phone=` sayfa biçiminde 0 veya 1 öğe döner (telefon okulda benzersiz; bulunamaması hata değil, API §1.6 liste biçimi); `phone` zorunlu ve geçerli olmalı. |
| DEC-155 | Students, Groups'un eklediği Enrollments sözleşmelerini kullanır (`Application/Enrollments/Contracts/EnrollmentContracts.cs`): tek `IEnrollmentEnder` hem grubun… |
| DEC-156 | `PUT /students/{id}/guardians` da `version` ister (API §1.7); sürümlü güncelleme öğrenci satırını her zaman günceller, böylece sadece veli bağlantısı değişse de… |
| DEC-157 | Aynı yeni veli telefonuyla eşzamanlı iki kayıt (veya veli değiştirme) ikisi de telefonu bulamayıp veliyi eklemeye çalışabilir; ikincisi `ix_guardians_tenant_id_phone`… |
| DEC-158 | `POST /students/{id}/leave` ve `/reactivate` `version` almaz (diğer aksiyon endpoint'leri gibi). |
| DEC-160 | Kayıt tarihleri: başlangıç isteğe bağlı (varsayılan okulun bugünü), bitiş zorunlu; ikisi de bugünden sonra olamaz ve 2000–2100 yılları arasında olmalı (aksi halde `400… |
| DEC-161 | `EnrollmentPolicy` kuralları şu sırayla uygulanır: öğrenci aktif değil, grup arşivli, zaten kayıtlı, doğum yılı aralık dışı (`year`/`from`/`to`), grup dolu, program… |
| DEC-162 | ADR-0005 uygulaması: `IEnrollmentLocks` ham SQL `SELECT id ... FOR UPDATE` çalıştırır, okul koşulu SQL'de açıkça yazılır (§5.4), her zaman önce öğrenci sonra grup;… |
| DEC-163 | Antrenör roster'ı (`GET /groups/{id}/roster`, US-14) sadece `firstName`, `lastName`, `birthYear`, `emergencyContactPhone` döner; öğrenci ID'si de yoktur. |
| DEC-164 | Oluşturma yanıtının `Location` hedefi için `GET /enrollments/{id}` (SA) eklendi (Ek D §2.8). `GET /groups/{id}/enrollments` sayfalıdır (en yeni başlangıç önce,… |
| DEC-165 | `Student.InvalidLeaveDate` (422): ayrılma tarihi öğrencinin herhangi bir aktif kaydının başlangıcından önceyse `StudentLeft` işleyicisindeki `IEnrollmentEnder` hatayı… |
| DEC-166 | Kilitlenme (deadlock) önlemi: toplu kayıt bitirme (grup arşivi, öğrencinin ayrılması) kayıtları ID sırasıyla günceller; yine de PostgreSQL bir deadlock'u (`40P01`)… |

#### Yoklama

| ID | Özet |
| --- | --- |
| DEC-170 | Yoklamada "o tarihte kayıtlı": `StartDate <= tarih <= EndDate` (bitiş günü dahil), bir istisnayla: öğrenci bitiş gününde başka bir kayda (başka grup veya aynı grubun… |
| DEC-171 | Antrenman anahtarı `YYYY-MM-DDTHH:mm` (yıl 2000–2100); bozuk anahtar rota parçası olduğu için alan hatası değil, doğrudan `400 Attendance.InvalidSessionKey`. |
| DEC-172 | `PUT .../attendance` gövdesi `{ records: [{ studentId, status }] }` (en fazla 1000): sadece listelenen öğrencilerin durumu yazılır, diğerleri korunur; durum silinemez. |
| DEC-173 | Aynı antrenmanın eşzamanlı kaydı: yoklama kaydı, antrenmanı okumadan önce `(grup, tarih, başlangıç)` için transaction kapsamlı bir PostgreSQL advisory lock alır… |
| DEC-174 | Geçmiş (US-21). |
| DEC-175 | Yoklama listesi satırı (`SessionStudentDto`) sadece `studentId`, `firstName`, `lastName`, `status` taşır (DEC-163'ün devamı, NFR-PRV-02); bir contract testi alan… |

#### Aidat ve duyuru

| ID | Özet |
| --- | --- |
| DEC-180 | Grup aidatları, program saatlerinden (DEC-140, jsonb) farklı olarak ayrı `groups.group_fees` tablosunda, `Group` aggregate'ının alt entity'si (`GroupFee`) olarak tutulur. |
| DEC-181 | Para API'de her yerde `MoneyDto` (`{ "amount": "1250.00", "currency": "TRY" }`, iki ondalıklı metin, DEC-066) olarak taşınır; girdi en fazla iki ondalık, nokta ayraçlı,… |
| DEC-182 | Ödeme: istekte öğrenci verilmez, kayıttan türetilir; `StudentId` ve `GroupId` (kaydın grubu hiç değişmez) raporlama ve filtre için denormalize tutulur, bileşik FK'lerle… |
| DEC-183 | Borçlar (US-24): `DueCalculator` kaydın en az bir gün aktif olduğu her ay için, okulun içinde bulunulan ayına kadar, o ayın aidatının tamamını borç sayar (kıst yok,… |
| DEC-184 | Billing yarışları: ödeme kaydı kaydın satırını `FOR NO KEY UPDATE` ile kilitler (`IEnrollmentDirectory.FindLockedAsync`), böylece eşzamanlı kayıt bitirme ödeme commit… |
| DEC-185 | Ödemenin denormalize öğrenci ve grubu, `(tenant_id, enrollment_id, student_id, group_id)` → `enrollments (tenant_id, id, student_id, group_id)` bileşik FK'siyle kayda… |
| DEC-186 | `billing.payments` üzerinde `trg_payments_guard` trigger'ı (migration'da `migrationBuilder.Sql`): hangi rolle bağlanılırsa bağlanılsın `DELETE` ve… |
| DEC-190 | Duyuru (US-25): Ek B'deki `CreatedBy`/`CreatedAt` domain'de `AuthorId`/`PublishedAt` adıyla tutulur, çünkü her tabloda aynı adlı gölge denetim sütunları vardır… |
| DEC-191 | Duyuru erişimi: yayınlama Okul Admini, grup var ve arşivlenmemiş olmalı (`404 Group.NotFound`, `422 Group.Archived`). `GET /groups/{id}/announcements` Okul Admini her… |
| DEC-192 | Duyuru başlık/metin uzunlukları, domain'in sakladığı biçimle aynı normalleştirmeden sonra doğrulanır (`Announcement.CleanTitle` boşlukları sadeleştirir, `CleanBody`… |

#### Dashboard, demo verisi ve testler

| ID | Özet |
| --- | --- |
| DEC-210 | Okul Admini ana sayfası `GET /dashboard` (P3-F4'ün öne alınmış MVP sürümü, Ek D §2.12) tek istekte döner; okulun büyüklüğünden bağımsız sabit sayıda sorgu (bugün 11),… |
| DEC-211 | Dashboard para alanları: "bu ay tahsil edilen" ve aylık serideki `collected` ödeme tarihine göredir (`paidOn` ayı, sadece `Recorded`; nakit esası), `due` ise o dönemin… |
| DEC-212 | Dashboard oranları 0–1 arası, 4 ondalık; bölünecek bir şey yoksa `null`. |
| DEC-220 | Demo verisi (P1-38) ayrı bir konsol projesi `src/SporAkademi.DemoSeed` ile yüklenir (`bash scripts/seed-demo.sh`), API'ye `seed-demo` komutu eklenmez: demo kodu API… |
| DEC-230 | Tenant izolasyon paketi (P1-35, NFR-SEC-08): `CrossTenantEndpointTests` uç noktaları elle seçmez, çalışan API'nin `EndpointDataSource`'undan okur ve B okulunu A okulunun… |
| DEC-240 | Uçtan uca testler (P1-36): Playwright (`@playwright/test` 1.64.1, Apache-2.0, yayın 2026-10-14; `playwright`/`playwright-core` 1.64.1 aynı gün) ve axe… |

#### P2-F3 Outbox

| ID | Özet |
| --- | --- |
| DEC-300 | Transactional outbox (P2-F3): dışarı çıkan yan etkiler (e-posta, webhook, ...) `outbox.messages` tablosuna iş değişikliğiyle aynı transaction'da yazılır ve commit'ten… |
| DEC-301 | Outbox serileştirmesi ve tip kaydı: System.Text.Json (web varsayılanları, camelCase, enum'lar adıyla, eksik constructor parametresi ve null'a izin verilmeyen alan hata). |
| DEC-302 | Outbox işleyicisi: `OutboxProcessor.ProcessBatchAsync` bir transaction'da `next_attempt_at <= şimdi` olan bekleyen mesajlardan bir parti (varsayılan 20) `ORDER BY… |
| DEC-303 | Outbox saklama ve gözlemlenebilirlik: işlenmiş mesajlar `Outbox:Retention` (7 gün) sonra silinir, temizlik `Outbox:CleanupInterval` (1 sa) aralıkla worker'da çalışır;… |
| DEC-304 | İlk outbox kullanımı: `StudentLeft` domain event'i `PublishStudentLeft` tüketicisiyle `students.student-left.v1` (`StudentId`, `LeftOn`) mesajına dönüşür;… |

#### P2-F7 Cache

| ID | Özet |
| --- | --- |
| DEC-310 | Cache (P2-F7): `HybridCache` (L1 süreç içi bellek + L2 dağıtık cache). |
| DEC-311 | Cache'lenenler: okulun branş ve tesis listeleri (`GET /sports`, `GET /facilities`; sayfa ve `includeArchived` başına), okulun saat dilimi (`ITenancyModule`; neredeyse… |
| DEC-312 | Tenant güvenliği: cache'e sadece `ITenantCache` üzerinden ve sadece `TenantCacheKey.For(okul, alan, ...)` ile oluşturulan anahtarla erişilir. |
| DEC-313 | Invalidation otomatik ve commit'ten sonradır: `CacheInvalidationInterceptor` (EF Core `ISaveChangesInterceptor` + `IDbTransactionInterceptor`) her kayıtta change… |
| DEC-314 | Ömürler (L2 / L1): okul ayarları 30 dk / 10 dk, branş ve tesis listeleri 30 dk / 1 dk, dashboard 1 dk / 30 sn. |
| DEC-315 | L2 isteğe bağlıdır ve isteği hiçbir zaman düşürmez: `ConnectionStrings:Cache` yoksa (testler, yerel çalıştırma, CI smoke) sadece L1 kullanılır. |
| DEC-316 | Yerel Valkey isteğe bağlıdır: compose'da `cache` profilindeki `valkey` servisi (`docker compose --profile cache up -d --wait`); düz `docker compose up`, `up postgres`,… |

#### P2-F6 Belgeler

| ID | Özet |
| --- | --- |
| DEC-320 | Belge deposu (P2-F6): dokümandaki MinIO yerine yerel geliştirme ve testlerde SeaweedFS (`chrislusf/seaweedfs:4.51`, yayın 2026-10-25, digest ile sabit, Apache-2.0; `weed… |
| DEC-321 | Yükleme akışı: dosya API üzerinden `multipart/form-data` ile gelir (önceden imzalı PUT ile doğrudan depoya değil). |
| DEC-322 | Tür ve boyut kuralları: Sağlık raporu PDF/JPEG/PNG, en fazla 5 MB, öğrenci başına en fazla 20 (`Document.LimitReached`, 422; yüklemeden önce hızlı bir sayım, ardından… |
| DEC-323 | İndirme: yetki kontrolünden sonra 5 dakikalık imzalı GET bağlantısı (`Storage:DownloadLinkLifetime`, 1 sn – 15 dk). |
| DEC-324 | Erişim: Okul Admini kendi okulunun öğrencilerinin belgelerini yükler, listeler, açar ve siler. |
| DEC-325 | Silme kalıcıdır (hard delete). |
| DEC-326 | Depo isteğe bağlıdır. |

#### P2-F2 Hangfire ve iş paneli

| ID | Özet |
| --- | --- |
| DEC-330 | Arka plan işleri (P2-F2) Hangfire ile: `Hangfire.Core`, `Hangfire.NetCore`, `Hangfire.AspNetCore` 1.8.25 (LGPL-3.0, yayın 2026-08-28) ve `Hangfire.PostgreSql` 1.21.1… |
| DEC-331 | Hangfire şeması migrator rolüyle oluşturulur, API'nin DDL yetkisi gerekmez: `AddHangfireSchema` migration'ı Hangfire.PostgreSql'in paket içindeki kendi kurulum… |
| DEC-332 | İş paneli (Hangfire Dashboard, `/jobs`) bütün okulların işlerini gösterdiği için sadece aktif Platform Admin açabilir. |
| DEC-333 | Tekrarlayan işler (hepsi UTC): `outbox-process` (`Jobs:OutboxCron`, varsayılan 10 sn'de bir `*/10 * * * * *`; dolu olmayan bir parti gelene kadar, en çok… |
| DEC-334 | Okul verisine dokunan iş sadece `TenantJobRunner.ForEachSchoolAsync` ile çalışır: okullar açıkça listelenir (aktif/askıda hepsi, ID sırasıyla) ve iş her okul için yeni… |
| DEC-335 | `/health/ready`'ye `jobs` kontrolü: bu süreç sunucu çalıştırıyorsa ve kendi sunucusunun (süreç başına benzersiz `ServerName`) heartbeat'i yoksa veya… |

#### P2-F1 Online ödeme

| ID | Özet |
| --- | --- |
| DEC-340 | Online aidat ödemesi (P2-F1) bir sağlayıcı soyutlamasıyla: `IPaymentProvider` (hosted checkout aç, sağlayıcıya durumu sor, webhook imzasını doğrula). |
| DEC-341 | Idempotency: `POST /online-payments` `Idempotency-Key` başlığı ister (8–100 karakter, `[A-Za-z0-9._:-]`; yoksa veya bozuksa `400`). |
| DEC-342 | Ödeme durum makinesi ve çift alacaklandırmanın önlenmesi: `OnlinePayment` (`billing.online_payments`) `Created → Pending → Succeeded / Failed / Expired`. |
| DEC-343 | Webhook'lar (`POST /api/v1/payment-webhooks`): önce `X-IYZ-SIGNATURE-V3` doğrulanır (iyzico'nun hosted checkout formülü `hex(HMACSHA256(secretKey, secretKey +… |
| DEC-344 | Anonim sağlayıcı uç noktaları ve tenant çözümü: `POST /payment-webhooks`, `POST /payment-callbacks` (tarayıcının hosted checkout'tan form `POST` ile dönüşü) ve `POST… |
| DEC-345 | iyzico adaptörü: resmi `iyzipay-dotnet` paketi (MIT lisanslı) yerine düz `HttpClient` (`IHttpClientFactory`, paylaşılan ASP.NET Core çerçevesinden; yeni paket yok):… |
| DEC-346 | Online ödemeler `POST /payments/{id}/cancel` ile iptal edilemez (`422 Payment.OnlineNotCancellable`; para gerçekten çekildi, iptal borcu ikinci bir tahsilata açardı) ve… |
| DEC-347 | Fake sağlayıcı: token ve sağlayıcı ödeme ID'si denemenin ID'sinden türetilir, durum bellekte tutulur (API yeniden başlarsa kaybolur), webhook'ları iyzico'nun hosted… |
| DEC-348 | Veli portalı (Faz 3) yokken asgari akış: Okul Admini "Ödenmemiş borçlar"da bir borç için Online ödeme başlatır (her tıklama yeni bir `Idempotency-Key`), uygulama onu… |
| DEC-349 | API ve hata kodları (Ek D §2.10, Ek E): `GET /online-payments`, `POST /online-payments`, `GET /online-payments/{id}` (SA); anonim `POST /payment-webhooks`, `POST… |

#### P2-F4 E-posta

| ID | Özet |
| --- | --- |
| DEC-350 | E-posta gönderimi (P2-F4): `IEmailSender` soyutlaması (Application) sadece outbox handler'larından, iş değişikliği commit edildikten sonra çağrılır; istek içinde e-posta… |
| DEC-351 | Hoş geldin e-postası: yeni bir hesap (`User.Create`) `UserCreated` domain event'ini çıkarır, `PublishAccountOpened` aynı transaction'da `identity.account-opened.v1`… |
| DEC-352 | Şifre belirleme bağlantısı (sıfırlama ve hoş geldin): 256 bit rastgele, base64url token; sadece SHA-256 hash'i `identity.password_tokens`'ta (benzersiz hash index'i,… |
| DEC-353 | `POST /api/v1/auth/password-reset/request` (`email`, anonim): biçimi uygun her istek `202 Accepted` ve boş gövde döner; hesabın var olup olmadığını, pasif olduğunu,… |
| DEC-354 | Admin arayüzü: `/forgot-password` ("Şifremi unuttum"; sadece çıkış yapmış kullanıcı, giriş sayfasında bağlantısı var) her durumda aynı onay metnini gösterir;… |
| DEC-355 | Aidat hatırlatmaları: günlük Hangfire işi `billing-dues-reminders` (`Jobs:DuesReminderCron`, varsayılan `0 6 * * *` UTC = 09:00 İstanbul),… |
| DEC-356 | Hatırlatmanın alıcısı: dokümanda velinin e-postası var (Ek B 4.7 `Guardian.Email?`) ama "aidat iletişim kişisi" yok. |
| DEC-357 | Yerel e-posta yakalayıcı Mailpit (`axllent/mailpit:v1.32.0`, digest sabit, MIT, yayın 2026-10-22): compose'da `mail` profilinde ve isteğe bağlı (`docker compose… |
| DEC-358 | Gizlilik (NFR-PRV-03): outbox mesajları sadece ID taşır (`UserId`, `ReminderId`, `RecipientId`); adres, ad ve bağlantı handler çalışırken okunur ve hiçbir yerde… |

#### P2-F5 Anonimleştirme

| ID | Özet |
| --- | --- |
| DEC-360 | Varsayılan saklama süresi (P2-F5): ayrılan öğrencinin kişisel verisi ayrılma tarihinden 24 ay sonra anonimleştirilir; ödeme kayıtları 10 yıl saklanır (TTK md. 82 ticari… |
| DEC-361 | Okul Admini sadece kişisel veri süresini değiştirir: `GET/PUT /api/v1/settings/data-retention` (xmin versiyonlu; rotada ve gövdede okul ID'si yok, her zaman kendi okulu). |
| DEC-362 | Anonimleştirme işi: Hangfire tekrarlayan işi `students-anonymization`, her gün 02:00 UTC (05:00 İstanbul, `Jobs:StudentAnonymizationCron`), `TenantJobRunner` ile okul… |
| DEC-363 | Neler silinir: ad `Anonim`, soyad `Öğrenci #` + ID'nin son 8 hex hanesi (UUIDv7'nin rastgele kısmı, zaman damgası değil), arama anahtarı yeniden hesaplanır; doğum… |
| DEC-364 | Her çalışma kaydedilir: `students.anonymization_runs` (okul, başlama zamanı, süre ms, kesim tarihi, uygulanan ay, anonimleştirilen ve ertelenen sayısı,… |
| DEC-365 | Veliler ve kullanıcılar: anonimleşen öğrencinin veli bağlantıları silinir; veli başka hiçbir öğrenciye bağlı değilse veli kaydı (ad, telefon, e-posta, adres) tamamen… |
| DEC-366 | Belgeler (P2-F6 ile): anonimleşen öğrencinin bütün belgeleri (sağlık raporları, fotoğraf) aynı transaction'da metadata olarak silinir ve her biri için mevcut… |
| DEC-367 | Ödemeler ve aidatlar: ödeme ve kayıt satırları silinmez, değişmez (tutar, dönem, ödeme tarihi, yöntem, durum kalır); kişisel veriyle bağ öğrencinin anonimleşmesiyle… |
| DEC-368 | Öğrencisi kalmayan veli silinir (KVKK veri minimizasyonu): `ReplaceGuardians` bir veliyi öğrenciden ayırınca `StudentGuardiansUnlinked` domain event'i yayınlanır; o veli… |

#### P2-F8 Aidat kuralları (2026-11-10)

| ID | Özet |
| --- | --- |
| DEC-370 | Aidat kuralları grup aidatı gibi sürümlüdür: dönem başına tek sürüm, geçmiş dönem reddedilir, sürüm yoksa hesap bugünküyle aynı; `GET/POST /api/v1/settings/billing-rules`. |
| DEC-371 | Kardeş = ortak veliye (geçişli) bağlı öğrenciler; o ayki toplam aidata göre sıralanır, en yüksek aidatlı tam öder, 2. kardeş %p2, 3. ve sonrası %p3; kuruşa yuvarlama. |
| DEC-372 | Grup değişikliğinde tek aidat: ay içinde biten kaydın ardından aynı ay (bitiş gününde veya sonra) başlayan kayıt o ay ücretlendirilmez; çakışan kayıtlar ikisi de öder; kıst yok. |
| DEC-373 | Tek hesap servisi (`DueCharges`): borç listesi, dashboard, online ödeme, elle ödeme ve hatırlatmalar aynı tutarı görür; borç satırı `listFee` ve `discount` taşır. |
| DEC-374 | Kardeşlik bugünkü veli bağlantılarından hesaplanır (geçmiş yok); kapanmış ayın borcu hiçbir yönde değişmez, yalnızca açık aylar (bu ay ve sonrası) güncel bağlantıları izler. |
| DEC-375 | Ay kapanışı: günlük `billing-close-periods` işi kural sürümü olan geçmiş ayların borçlarını `billing.charge_snapshots`'a dondurur, ayı `billing.closed_periods`'a yazar; tablolar değiştirilemez. Kapanmış aya geç eklenen kardeş dondurulmuşlardan sonra sıralanır; kapanmayan ay `/health/ready`'de `billing-close` uyarısı verir. |
| DEC-376 | Form gelecek ayı önerir, bu ay seçilirse onay ister; başlamamış sürüm `DELETE /api/v1/settings/billing-rules/{effectiveFrom}` ile geri alınır, başlamış sürüm `422 BillingRules.AlreadyStarted`. |

#### Oturum dayanıklılığı (2026-11-12)

| ID | Özet |
| --- | --- |
| DEC-446 | Refresh token tekrarında 30 sn tolerans: rotasyondan sonraki 30 sn içinde ve halef hiç kullanılmamışken eski token aynı ailede yeni token alır, hiçbir şey iptal edilmez; pencere dışında ya da halef kullanılmışsa bütün oturumlar kapanır (DEC-405'teki "sunucu penceresi yok" kararını değiştirir). |
| DEC-447 | Admin açılışı yalnız `401`/`403`'te oturumsuz açılır; ağ, `409`, `5xx` hatasında 500 ms sonra bir kez yeniden dener (`429` denemez); 5 sn bekleme refresh isteğini iptal etmez, geç başarı (giriş başlamadıysa) giriş sayfasından `returnUrl`'e götürür; o arada başlayan giriş eski geri yüklemenin sonucunu düşürür ve çağrının bitmesini (en çok 15 sn) bekler. 15 sn sonrası kalan risk istemcide kapatıldı: refresh token'ının `sub`'ı oturumdaki kullanıcıyla uyuşmazsa oturum biter (`/login?expired=1`); interceptor dönemi istek gönderilirken yakalar, eski dönemin `401`'i yeni girişi bitirmez. |

## 6. Sapmalar / bilgilendirme

- **`docker image inspect` (P2-F6).** P2-F6 sırasında bir agent, herkese açık SeaweedFS imajı üzerinde bir kez `docker image inspect` çalıştırdı (sadece metadata; belirtilen kurala aykırıydı). Tekrarlanmadı.
- **gitleaks bütün dalları tarar.** Tarama tüm git geçmişini ve bütün dalları kapsadığı için sentetik test şifreleri izin listesine dar biçimde alındı: her `[[allowlists]]` girdisi tek bir sentetik değeri sadece onu tutan dosyalarda kabul eder (`condition = "AND"`, `targetRules = ["generic-api-key"]`, sabit `^…$` kalıpları; DEC-250). Yeni bir sentetik değer kendi girdisiyle eklenir; gerçek sızıntı listeye eklenmez.
- **gitleaks CI çıktısı.** CI'daki gitleaks artık `--verbose` ile `--redact` birlikte çalışır: bulgunun kuralı, dosyası ve satırı görünür, sır değeri maskelenir.
- **Otonom merge (DEC-098).** PR #43 – #65, dört zorunlu check yeşil ve bağımsız agent incelemesiyle agent tarafından squash merge edildi; inceleme özetleri PR yorumlarındadır.
- **iyzico gerçek sandbox'ta çalıştırılmadı.** Anahtar olmadığı için yalnızca sahte HTTP handler'la test edildi (bkz. "Tek seferlik işlemler").
- **CI `backend` süre sınırı 15 → 25 dakika (P2-F8 PR'ı, 2026-11-10).** Entegrasyon paketi (~740 test, her biri ayrı API host'u, sıralı) hosted runner'larda 8–13,5 dakika sürüyor; restore ve build'le birlikte 15 dakika sınırı `main`'de bile (6a47840, paket geçmişken) ve P2-F8 dalında iki kez işi iptal etti. Asıl çözüm paketi hızlandırmak (paylaşılan host veya paralel koleksiyonlar); fikir olarak açık.
- **Roadmap ve CHANGELOG.** Faz geçişi kapıları ve etiketler işaretlenmedi; Faz 2 `v0.2.0` etiketi sahipte.

## 7. P2-F8 aidat kuralları: onay bekleyen varsayılanlar

Soru 17'ye cevap olarak sahip "önerilen varsayılanlarla ilerle" dedi (2026-11-10). Aşağıdakiler kodda böyle çalışıyor; her biri için **onayla** veya **değiştir**. Ayrıntı: DEC-370 – DEC-376.

1. **İndirim oranları.** Kurallar okul bazında ve bir aydan itibaren tanımlanıyor; formdaki varsayılan **2. kardeş %10, 3. ve sonraki kardeşler %15**. Okul 0–100 arası istediği oranı girebilir (0 = kapalı). Bu varsayılanlar uygun mu?
2. **Kim tam öder (sıralama).** O ay en yüksek toplam aidatlı kardeş tam öder; eşitlikte en erken kayıt başlangıcı, sonra öğrenci ID'si. İndirim, sıralamadaki kardeşin o ayki **bütün** aidatlarına uygulanır; ücretsiz gruptaki kardeş sıra almaz. Kardeş = en az bir ortak veli (geçişli). "En pahalı çocuk tam öder" kuralı uygun mu, yoksa ör. en büyük/ilk kaydolan çocuk mu tam ödesin?
3. **Grup değişikliği.** Ay içinde grup değiştiren öğrencinin o ayı yalnızca eski grubun aidatıyla ücretlendiriliyor (yeni grup ertesi aydan; kıst yok, DEC-017). İki gruba aynı anda kayıtlı öğrenci ikisini de öder. Okul bu kuralı kapatabilir (o zaman iki kayıt da o ayı öder). Uygun mu, yoksa ay yeni grubun (veya yüksek olanın) aidatıyla mı ücretlendirilsin?
4. **Geçmiş aylar ve bağlantı geçmişi.** Kardeşlik bugünkü veli bağlantılarından hesaplanıyor (bağlantı geçmişi yok). Bunun yerine kural sürümü olan her geçmiş ay, bittikten sonraki gece kapanıyor ve borçları donduruluyor (DEC-375): **kapanmış bir ayın borcu hiçbir yönde değişmez**; veli ayrılsa veya silinse, öğrenci anonimleşse ya da sonradan yeni kardeş bağlansa da ödenmiş ay yeniden borçlanmaz, gecikme e-postası gitmez, fazla ödeme oluşmaz. Değişebilen yalnızca **açık aylardır** (içinde bulunulan ay ve sonrası): bağlantı değişirse bu ayın borcu güncel bağlantılara göre yeniden hesaplanır. Bu ayrım uygun mu, yoksa bağlantı geçmişi (geçerlilik tarihli veli bağlantısı) da istenir mi?
5. **Bu ay için kural ve geri alma (DEC-376).** Form gelecek ayı öneriyor; bu ay seçilirse bu ayın açık borçlarının hemen değişeceği söylenip onay isteniyor. Başlamamış (gelecek ay) bir sürüm geri alınabiliyor, başlamış sürüm alınamıyor (hata bir sonraki ay için yeni sürümle düzeltilir). Uygun mu?

## 8. Faz 2 sürüm hazırlığı (2026-11-11)

1. **e2e ortamı (DEC-241).** Online ödemenin uçtan uca testi için e2e API'si `Production` yerine `Staging` ortamında, açıkça `Payments__Provider=Fake` ile çalışıyor (Fake Production'da hiç seçilemez). Kodda iki ortam arasında e2e'yi etkileyen başka fark yok; Production yapılandırma kuralları entegrasyon testlerinde kalıyor. Öneri: onayla. Alternatif: Production'da kalıp online ödeme e2e'sini ayrı bir Staging API'siyle çalıştırmak (ikinci sunucu ve porta değer bulunmadı).

## 9. Faz 2 geçişi ve açık soruların cevabı (2026-11-11)

Sahip **Faz 2 geçişini onayladı** (2026-11-11). Bölüm 7 ve 8'deki açık soruları spor okulu yöneticisi, antrenör ve yazılımcı gözüyle agent'ın cevaplamasını istedi. Cevaplar aşağıda; ilgili kararlar `Accepted` yapıldı. `v0.2.0` etiketi ve hukuki teyitler sahipte kalır.

**Bölüm 7 (P2-F8):**

1. **İndirim oranları: onay (%10 / %15).**
   - *Yönetici:* Türkiye'deki futbol ve basketbol okullarında kardeş indirimi çoğunlukla %10–%20 arasındadır. %10 / %15 kasayı zorlamaz, veliye de anlamlı gelir.
   - Oran okul bazında değiştirilebiliyor. Kampanya dönemleri yeni bir sürümle karşılanır.
2. **En yüksek aidatlı kardeş tam öder: onay.**
   - *Yönetici:* İndirimin ucuz gruba düşmesi okulun gelirini korur, yaygın uygulama da budur. Alternatif olan "en büyük çocuk tam öder" kuralında, büyük çocuk ucuz bir gruptaysa indirim pahalı gruptaki kardeşe uygulanır ve okul gereksiz gelir kaybeder.
   - *Yazılımcı:* Sıralama deterministik; eşitlikte en erken kayıt, sonra öğrenci kimliği belirliyor. Veli itiraz ederse borç satırındaki "Kardeş indirimi %10" ipucu neyin neden uygulandığını gösteriyor.
3. **Grup değişikliğinde o ay eski grubun aidatı: onay.**
   - *Antrenör:* Ay ortasında üst gruba alınan çocuk çoğunlukla gelişim nedeniyle terfi eder. Veliye o ay ek fark çıkarmak yanlış mesaj verir.
   - *Yönetici:* Kıst hesabı (DEC-017) tahsilatı karmaşıklaştırır. Ertesi ay yeni grubun aidatı başlar. Kuralı uygun bulmayan okul onu kural sürümünde kapatabilir.
   - İki gruba aynı anda kayıtlı öğrencinin ikisini de ödemesi doğru, çünkü iki ayrı hizmet alıyor.
4. **Kapanmış ay değişmez, bağlantı geçmişi tutulmaz: onay.**
   - *Yönetici:* Muhasebe açısından kapanmış ay kapanmıştır. Sonradan bağlanan kardeş için geriye dönük indirim ya da borç doğmaması veliyle tartışmayı önler.
   - *Yazılımcı:* Geçerlilik tarihli veli bağlantısı veri modelini ve bütün sorguları ağırlaştırır. Ay kapanışı aynı güvenceyi daha basit sağlıyor. İhtiyaç doğarsa Faz 4 adayıdır.
5. **Bu ay için onaylı kural, başlamış sürümün geri alınamaması: onay.**
   - *Yönetici:* Ay ortasında geriye dönük fiyat değişikliği velide güven kaybı yaratır. Onay penceresi yanlışlıkla yapılmasını önlüyor; hata bir sonraki ayın sürümüyle düzeltilir.

**Bölüm 8:**

1. **e2e ortamı Staging + Fake (DEC-241): onay.**
   - *Yazılımcı:* Fake sağlayıcı Production'da yapılandırma doğrulamasıyla engelli ve bu entegrasyon testleriyle kanıtlı. E2e'nin dayandığı başka hiçbir davranış ortama bağlı değil (bağımsız inceleme, PR #78).
   - İkinci bir API sunucusu e2e süresini ve karmaşıklığını artırırdı, karşılığında bir kazanç sağlamazdı.

**Faz 3 için not:** soru 13'teki "Admin paylaşımlı ödeme bağlantısı", veli portalı (P3-F2) geldiğinde velinin kendi ödemesiyle tamamlanır. Admin bağlantısı, e-postası olmayan veliler için kalır.

## 10. P3-13 online ödeme iadesi (2026-11-12)

Soru 14'ün Faz 3 adımı. Sahip kararları agent'a bıraktı (futbol okulu yöneticisi ve para işleyen yazılımcı gözüyle); kararlar DEC-416 – DEC-425, `Accepted (agent, sahip yetkisiyle 2026-11-12)`. Sahibin bakması iyi olacak noktalar:

1. **Yalnız tam iade, yalnız online ödeme (DEC-416).** Kısmi iade yok; nakit ve havale elle iade edilip kaydı iptal edilir.
   - *Yönetici:* veliler iadeyi ay bazında ister; fazla ödeme de bir denemenin tamamıdır.
2. **İade edilen ödeme borçtan düşer (DEC-420).** Kayıt o ay hâlâ aktifse borç yeniden açılır. Fazla ödemenin iadesinde borç ödenmiş kalır. Erken bitirilmiş peşin ödemede borç oluşmaz.
3. **Kapanmış ayın ödemesi de iade edilebilir (DEC-421).** Donmuş borç değişmez, borç yeniden açık görünür. Yasaklamak okulu sistem dışı iadeye iterdi.
4. **Erken bitirmede uyarı, otomatik iade yok (DEC-422).** Kayıt bitirme penceresi peşin ödenmiş ayları listeler; iade kararı okulda kalır.
5. **Açık konu (DEC-418, DEC-424):** iyzico iadesi gerçek sandbox'ta denenmedi (anahtar yok). Sonucu kaybolan iade artık ödeme detayıyla kurtarılıyor: iyzico'nun her reddi kontrol ediliyor, kesin değilse iade "İnceleme gerekli" olarak bekliyor ve yönetici "Sağlayıcıda kontrol et" ile sonuçlandırıyor. iyzico iade uç noktası, ret kodları ve raporlama API'sinin alanları gerçek sandbox denemesinde DEC-345'teki imza konusuyla birlikte doğrulanmalı.
