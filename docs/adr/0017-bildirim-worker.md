# ADR-0017 — Bildirim tüketicileri ayrı bir worker'da

- **Durum:** Proposed (agent, 2026-11-17). Sahip onayı Pazartesi onay listesinde (§12, P5-04).
- **İlgili:** P5-04, DEC-630 – DEC-639, ADR-0016 (RabbitMQ), ADR-0001 (modüler monolit), ADR-0009 (domain event'leri), DEC-300 – DEC-303 (outbox)

## Bağlam

ADR-0016 ile outbox mesajları RabbitMQ'dan tüketiliyor ve tüketiciler hosted service olarak, host ve modül başına seçilebiliyor.
Faz 5 planı, bildirim tüketicilerinin ayrı bir süreçte çalışmasını ister: dağıtık sistem tekniğini (ayrı servis, ayrı imaj,
ayrı ölçekleme) bu projenin ölçeğinde, bedeliyle birlikte denemek ve ne zaman değmeyeceğini öğrenmek. Bu bir zorunluluk değil:
e-posta zaten outbox ile istekten ayrılmıştı ve tek süreçte doğru çalışıyordu.

## Karar

`src/SporAkademi.Notifications`: Application ve Infrastructure katmanlarını aynı DI ile kullanan, yalnız `/health/live` ve
`/health/ready` sunan küçük bir web host. Yalnız **e-posta** gönderen tüketicileri (altı handler, `notifications` tüketici grubu)
RabbitMQ'dan çalıştırır. Kendi imajı (`docker/notifications`), kendi Container App'i (`infra/`), OTLP ve Sentry'si vardır.

```
API ──(komut + outbox, tek transaction)──► OutboxProcessor ──► RabbitMQ ──► "<tür>.<handler>" kuyrukları
  │                                                                              │
  │  grupsuz handler'lar (SignalR push, belge silme, günlük)                     │ notifications grubu
  └──────────────── API'nin tüketicileri ◄────────────────────────────────────── ┴──► Notifications worker ──► SMTP
```

- **Seçim bir grupla yapılır, modülle değil** (DEC-630). Mesaj türünün modülü (`identity`, `billing`) yetmedi: e-posta
  handler'ları iki modüle dağılmış ve yanlarında API'de kalması gereken handler'lar var. Handler sınıfındaki
  `[OutboxConsumerGroup("notifications")]` ve `RabbitMq:Consumers:Groups` / `ExcludeGroups` bunu çözer. Bir mimari test,
  `IEmailSender` ya da `PasswordLinkMailer` alan her handler'ın etiketli olmasını ve yalnız onların etiketli olmasını zorlar.
- **API'de dışlama yapılandırmayla** (DEC-631): worker yayındayken API `ExcludeGroups=notifications` alır. `ConnectionStrings:RabbitMq`
  yoksa hiçbir şey değişmez; outbox e-postayı API sürecinde gönderir. Ayar unutulursa çift e-posta olmaz (iki süreç aynı
  kuyruğu bölüşür, inbox tüketici başına), yalnız e-postanın hangi süreçten çıktığı belirsizleşir.
- **Worker'ın kapsamı kodla sabit** (DEC-632): yalnız `notifications` grubu, Hangfire sunucusu kapalı, outbox yayımı kapalı;
  bir yapılandırma bunları açamaz. Brokersiz başlamaz. `/health/ready` Degraded'ı 503 sayar.

## Neden yalnız bildirimler (ve neden yalnız e-posta)

- **Kimseye anında görünmez:** e-posta zaten asenkron. Ayrı sürece taşımak kullanıcıya yeni bir gecikme eklemez, bir
  tutarlılık sözleşmesini de değiştirmez (outbox ve inbox ADR-0016'dan beri aynı).
- **Dış bağımlılığı olan tek handler grubu:** SMTP yavaşlar, zaman aşımına uğrar, kota koyar. Bu iş artık API'nin
  süreç ve bellek payını, iş parçacıklarını ve yeniden başlatma döngüsünü paylaşmıyor.
- **Durumsuz:** handler, kendi sürecinde tutulan bir şeye ihtiyaç duymaz; veritabanı ve sözleşme yeter.
- **Ölçeklenme profili farklı:** aidat hatırlatması günde bir kez toplu mesaj üretir; API'nin yük profili bundan bağımsız.

**SignalR push'ları API'de kalır** (DEC-637). Hub ve canlı bağlantılar API sürecindedir. Worker'ın hub'a ulaşması için ya API'yi
ağdan çağırması (yeni bir iç API, kimlik ve yeniden deneme) ya da kendisinin bir hub'a ve backplane'e sahip olması gerekirdi;
istemcinin bağlı olduğu süreçten ayrı bir süreçte push üretmenin kazancı yok. Realtime handler'ları worker'da çalışsaydı
`NoRealtimeNotifier` ile mesajı ack edip push'u sessizce yutardı; bu yüzden worker'ın grubu kodla sabittir.

**Belge silme, günlük handler'ları** da API'de kalır: ayrılmaları için bir neden yok.

## Ödünleşimler

| Konu | Kazanç | Bedel |
| --- | --- | --- |
| **Ağ ve operasyon** | SMTP'nin yavaşlığı API'yi etkilemez | Bir imaj, bir Container App, bir probe seti, bir izleme hedefi daha; broker ve veritabanı bağlantı ayarları iki yerde; e-posta ayarları (`email_env`) ikisinde de tutulur (süreç içi modda API gönderir) |
| **Eventual consistency** | Yok: değişmedi | Ek gecikme kuyruk + worker kadar; worker kapalıyken e-posta kuyrukta bekler (kayıp yok), bu sürede "gönderildi" varsayılamaz |
| **Hata ayıklama** | Tüketici span'i yayımcının trace'ine bağlanır (W3C başlığı), bir e-posta tek iz | Log ve iz iki süreçte; broker yönetim arayüzü ve ölü mektup kuyruğu elle işlenir; worker'da API'nin sağlık gauge'ları yok |
| **Dağıtım bağlaşıklığı** | Bağımsız dağıtılabilir | Ortak veritabanı ve ortak mesaj sözleşmesi: şema ya da mesaj değişikliği iki imajı birlikte düşündürür; migration önce, ardından iki süreç (eski ve yeni sürüm bir arada çalışır, DEC-629). İmaj aynı katmanlardan derlendiği için sürüm kayması "iki farklı kod" riski değil "iki farklı sürüm" riski |
| **Bağımsız ölçekleme** | Kuyruklar rakip tüketicilerle bölüşülür; kopya sayısı değişkendir | Gerçek ölçekleme sinyali (kuyruk derinliği ile KEDA kuralı) bu PR'da yok; varsayılan 1 kopya |
| **Hata yalıtımı** | Worker çökerse API sürer; API çökerse e-postalar gider | Worker çalışmıyor diye uyarı yoksa e-postalar sessizce birikir (broker alarmı ve `rabbitmq` kontrolü bunu görünür kılar) |
| **Maliyet** | — | Sürekli açık ek bir kopya (0,25 vCPU, 0,5 GiB); sayı DEC-648'in yöntemiyle çıkarılabilir, bu PR'da hesaplanmadı |
| **Telemetri kodu** | Gizlilik süzgeçleri (`TelemetryScrubber`, `AccessTokenRedaction`, `SentryPrivacy`) Infrastructure'da tek yerde, iki host aynı sınıfı kullanır | Host kurulumu (OTLP ve günlük ayarları) iki yerde; worker'da API'nin sağlık gauge'ları yok (DEC-634) |

## Bu işte bulunan sorun

Worker'ın ilk integration testi, ADR-0016'daki tüketici kataloğunun handler türlerini öğrenmek için **her handler'ı oluşturduğunu**
ortaya çıkardı. `DeleteDocumentObject` belge deposunu ister; ayrı süreçte depo ayarı yok, katalog düştü ve tüketici hizmeti
hostu durdurdu (DEC-636). Katalog artık servis kayıtlarını okur. Ayrı bir süreç, tek süreçte hiç görünmeyen "bu handler'ın
bağımlılıkları burada yok" sınıfı hataları ortaya çıkarır; bu da ayrı servisin gerçek bir bedelidir.

## Farklı yapsaydık

- **Etiket yerine ayrı mesaj türü ya da modül:** e-posta handler'larını kendi `notifications` modülüne taşımak sınırı daha temiz
  yapardı, ama mesaj adları saklı ve kuyruklarda yaşayan bir sözleşme (ADR-0016) ve modüller arası iletişim yeniden
  tasarlanırdı. Etiket ucuz ve geri alınabilir.
- **Telemetri kurulumunu da paylaşmak:** gizlilik süzgeçleri paylaşılıyor ama OTLP ve günlük kurulumu iki hostta ayrı yazıldı; ilk
  sürümde süzgecin de kopyası vardı ve daha zayıftı (inceleme yakaladı). Süzgeçler Infrastructure'a taşındı; üçüncü bir süreç gelirse
  kurulum için de ortak bir `Hosting` projesi çıkarılmalı.
- **Compose servisi:** yerelde worker yalnız betikle çalışır; compose'a eklemek `docker compose up` ile tam kurulumu kolaylaştırırdı
  ama imaj derlemesini yerel döngüye sokardı.
- **Kuyruk derinliğine göre ölçekleme** (KEDA) baştan yazılabilirdi; hacim bilinmediği için ertelendi.

## Ne zaman bölünmez

- Tek ekip, tek dağıtım hattı, düşük hacimde: ayrı süreç kazancı küçük, işletme yükü kalıcıdır. Bu proje bugün tam bu durumda;
  bu ADR'nin değeri kararın kendisinden çok bedelin ölçülmesidir. `enable_notifications_worker=false` her şeyi tek süreçte
  çalıştırır ve desteklenen bir durumdur.
- İş, istek sürecindeki duruma ya da bağlı bağlantılara ihtiyaç duyuyorsa (SignalR, bellek içi önbellek): sınır kazanç getirmez.
- Okuma-yazma tutarlılığı gerekiyorsa (bir işlemin hemen ardından sonucunu görmek): eventual consistency kabul edilemez.
- Mesaj sözleşmesi hâlâ sık değişiyorsa: iki imajı koordine etmek her değişiklikte maliyet.
- Yalnız "mimari olarak temiz" diye: bölmenin gerekçesi hata yalıtımı, bağımsız ölçek ya da farklı yaşam döngüsü olmalı.

## Sonuçlar

- Yeni paket yok. Yeni proje: `SporAkademi.Notifications`; yeni imaj: `spor-akademi-v2-notifications` (GHCR).
- API'de ek yapılandırma: `RabbitMq:Consumers:ExcludeGroups`. Worker için: `ConnectionStrings:App`, `ConnectionStrings:RabbitMq`,
  `Email:*`, isteğe bağlı `OpenTelemetry:*`, `Sentry:Dsn`.
- Açık: kuyruk derinliğine göre ölçekleme; worker için sağlık gauge'ları; worker'ın uyarıları (kuyrukta bekleyen e-posta yaşı);
  gerçek hacimde ölçüm.
