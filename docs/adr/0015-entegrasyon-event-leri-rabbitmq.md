# ADR-0015 — Entegrasyon event'leri için RabbitMQ

- **Durum:** Proposed (agent, 2026-11-17). Sahip onayı Pazartesi onay listesinde (§12, P5-03).
- **İlgili:** P5-03, P5-04 (bildirim tüketicilerinin ayrı worker'a taşınması), DEC-300 – DEC-303 (outbox), DEC-620 – DEC-629,
  ADR-0003 (multi-tenancy), ADR-0009 (domain event'leri), DEC-551 (izleme)

## Bağlam

Entegrasyon event'leri (e-posta, gerçek zamanlı bildirim, belge silme, ödeme sonrası işler) bugün transactional outbox'a
yazılır ve aynı API sürecinde, Hangfire işi ya da polling worker'ı tarafından handler'lara teslim edilir. Bu, tek süreçte
doğru ve basit. Faz 5'te bildirim tüketicileri ayrı bir worker sürecine taşınacak (P5-04); bunun için süreçler arası, kalıcı
bir taşıyıcı gerekir. Taşıyıcı, outbox'ın güvencesini (iş değişikliğiyle birlikte kaydedilen mesaj kaybolmaz) bozmamalı ve
çok kiracılılığı (mesajı alan okul bağlamında çalışır) taşımalı.

## Karar

Outbox **kalır**; broker, outbox'ın teslim yolu olur. `ConnectionStrings:RabbitMq` yoksa bugünkü süreç içi teslim aynen
çalışır (varsayılan). Varsa `OutboxProcessor` handler çağırmaz, mesajı RabbitMQ'ya yayımlar; tüketiciler handler'ları
kendi kuyruklarından çalıştırır. İstemci `RabbitMQ.Client` 7.x (Apache-2.0), broker RabbitMQ 4.x (MPL-2.0).

```
komut + outbox satırı (tek transaction)
   → OutboxProcessor → [publisher confirm] → exchange spor-akademi.events (topic, anahtar = mesaj türü)
   → kuyruk "<tür>.<handler>" (quorum) → tüketici: inbox kontrolü + handler (tek transaction) → ack
          └ hata → "<kuyruk>.retry" (TTL) → asıl kuyruk … → MaxDeliveryAttempts → dead-letter kuyruğu
```

## Neden RabbitMQ

- **Gereksinim küçük ve belli:** kalıcı kuyruk, handler başına bağımsız tüketim, yeniden deneme, ölü mektup. Bunlar AMQP'nin
  temel özellikleri; Kafka'nın log/partition modeli (tekrar oynatma, çok yüksek hacim) bu ölçekte işletme yükü katar.
- **Yerelde ve testte aynı:** tek imaj, Testcontainers ile gerçek broker; bulut hesabı gerektirmez. Azure Service Bus
  emülatörü bu ortamda yok; Service Bus'a özgü kod yerel geliştirmeyi ve CI'yı buluta bağlardı.
- **Taşınabilirlik:** AMQP 0-9-1 yönetilen servislerde (CloudAMQP, Azure'da kendi VM'imiz) de var. Azure'a geçişte ana seçenek
  Service Bus kalır (yönetilen, işletme yükü yok); mesaj sözleşmesi, outbox ve idempotent tüketici broker'dan bağımsız
  olduğundan taşıyıcıyı değiştirmek `OutboxProcessor`'ın yayım çağrısı ile tüketici hizmetinin değişmesidir.

## Neden MassTransit değil

MassTransit v9 ve sonrası ticari lisanslı; projenin kuralı ticari lisanslı paket kullanmamak. v8 (Apache-2.0) bakım dışı
kalacak bir dala bağlar. Ayrıca bu projede MassTransit'in sunduğu şeylerin çoğu (outbox, retry, DLQ, saga) zaten var ya da
gereksiz; geriye kalan, yaklaşık 700 satırlık küçük bir yayımcı ve tüketici hizmetidir ve tamamı bu repoda okunur.
**Maliyet:** topolojiyi, yeniden bağlanmayı ve hata yollarını kendimiz yazıyoruz ve test ediyoruz (Testcontainers testleri
bunun için).

## Teslim garantileri

- **At-least-once.** Yayım, publisher confirm'den sonra işlenmiş sayılır; confirm ile outbox commit'i arasındaki çökme
  mesajı iki kez yayımlar. Tüketici de ack öncesi çökerse mesaj yeniden gelir. **Exactly-once yoktur**; yerine tüketici
  idempotency'si vardır.
- **Idempotent tüketici:** `outbox.inbox` `(consumer, message_id)` satırı, handler'ın kendi transaction'ında yazılır
  (DEC-623). Veritabanı içi etkiler tam bir kez olur. Dış etkiler (e-posta) transaction'a katılamaz; handler'lar `MessageId`
  ile korunur (DEC-302).
- **Kayıp yok:** kuyruklar kalıcı ve quorum, mesajlar persistent, yayımcı confirm bekler ve `mandatory` kullanır; broker
  kapalıyken mesaj outbox'ta kalır ve deneme hakkı tüketmeden tekrar denenir (DEC-622).
- **Zehirli mesaj:** `MaxDeliveryAttempts` sonra dead-letter kuyruğuna gider; alarm ve runbook vardır (DEC-627).

## Sıralama

Genel sıra garantisi **yoktur**: yeniden deneme mesajı sonradan teslim eder, her handler kendi kuyruğundan bağımsız tüketir ve
bir kuyrukta birden çok tüketici/yeniden teslim sırayı bozabilir. Bu kabul edilebilir çünkü mesajlar yalnız ID taşır ve
handler'lar çalıştıkları andaki veritabanı durumuna bakar. Bir gün sıra şart olan bir tüketici gelirse çözüm: mesaja
sürüm/sıra numarası koymak ve eski olanı atlamak (yeni sürüm kuralı), tek tüketicili kuyruk ya da single-active-consumer.

## Mesaj sözleşmesinin sürümlenmesi

Mesaj adı (`module.event.vN`) saklı ve kuyruklarda yaşayan bir sözleşmedir. Alan eklemek uyumludur ama eksik alanı reddeden
serileştirme (`RespectRequiredConstructorParameters`) yüzünden yeni alan nullable ya da varsayılan değerli olmalı; alan
silmek ya da anlam değiştirmek `vN+1` adıyla yeni tür ister. Rolling deploy'da eski ve yeni sürüm bir arada çalıştığından
eski ad, kuyrukları boşalana kadar tüketilmeye devam eder (DEC-629). Bilinmeyen tür ya da payload tüketiciyi düşürmez, ölü
mektuba gider.

## Sonuçlar

- Yeni bağımlılık: `RabbitMQ.Client` 7.2.1; yeni altyapı: RabbitMQ (compose `messaging` profili, production'da Deploy bloğu).
- Broker tek hata noktası olabilir; kesinti outbox'ta birikir, `rabbitmq` hazırlık kontrolü ve alarm bildirir.
- Tüketiciler modül başına seçilebilir (`RabbitMq:Consumers:Modules`); P5-04 bildirim modülünü ayrı sürece yapılandırmayla taşır.
- Açık: outbox satırı üreten isteğin iz bağlamını saklamıyor (iz teslim işinden başlar); dead-letter kuyruğundan geri yükleme
  şimdilik yönetim arayüzüyle (shovel) elle yapılır.
