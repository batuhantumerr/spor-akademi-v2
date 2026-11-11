# ADR-0013 — Okul başına alt alan adı (kadikoy.sporakademi.example)

- **Durum:** Accepted (agent, sahip yetkisiyle 2026-11-11). Karar: **şimdi yapılmaz**; tek alan adı, tenant token'dan.
- **İlgili:** P3-12 (P3-F7), DEC-381, DEC-004, DEC-087, DEC-344, ADR-0003, ADR-0006 ([proje dokümanı Ek F](../proje-dokumani.md)), [ADR-0012](0012-veli-uygulamasi-teknoloji-yigini.md)

## Bağlam

ADR-0003 tenant çözümlemesi için "alt alan adı" seçeneğini faz 3'te değerlendirilmek üzere açık bıraktı. Soru: her okul kendi adresinden (`kadikoy.sporakademi.example`) mi açılsın?

Bugünkü durum (koddan ve dokümandan):

- **Tenant kaynağı:** JWT'deki `tenant_id` claim'i (`HttpCurrentUser`). Giriş e-posta + şifreyle yapılır; e-posta platform genelinde benzersizdir ve kullanıcı tek okula bağlıdır (DEC-004). Kullanıcı hangi okulda olduğunu söylemez, sistem bilir. Platform Admin'in tenant'ı yoktur.
- **Anonim tek yol:** Ödeme sağlayıcısının webhook'u ve dönüş çağrısı okul taşımaz; okul, token'ın sahibi `OnlinePayment` kaydından okunur (`CheckoutTenantResolver`, DEC-344). Adres okul içermez: `/api/v1/payment-callbacks`.
- **Oturum:** access token bellekte, refresh token `sa_refresh` çerezinde: `HttpOnly; Secure; SameSite=Strict; Path=/api/v1/auth`, `Domain` yok (host-only). Refresh ve çıkış `POST` + `X-Requested-With` ister (DEC-053, DEC-055). Angular ve React uygulaması API ile aynı origin'dir (`/api`); CORS kapalıdır.
- **Sunum:** `docker/admin/nginx.conf` ve `docker/parent/nginx.conf` `server_name _` ile host'tan bağımsız statik SPA sunar. Planlanan production: tek VPS, Caddy, `app.` ve `staging.` alt alan adları (DEC-087).
- **E-posta bağlantıları:** tek `Email:AppBaseUrl` (`EmailOptions`); şifre belirleme ve sıfırlama bağlantıları buna eklenir. Ödeme `CallbackUrl` ayarı da tek mutlak adrestir ve sağlayıcıya (iyzico sandbox) kaydedilir.
- **Marka:** okula özel görünüm yok; tema token'ları iki uygulamada ortak ve sabit (ui-tema.md). Dokümanda alt alan adı, white-label veya özel alan adı talebi geçmiyor.

## Değerlendirilen seçenekler

| Seçenek | Özet |
| --- | --- |
| A. Tek alan adı, tenant token'dan (bugün) | Hiçbir şey değişmez. |
| B. Okul başına alt alan adı, token'a ek doğrulama | Host'taki slug ile `tenant_id` claim'i eşleşmezse reddedilir. Wildcard DNS ve TLS gerekir. |
| C. Alt alan adı tenant'ın tek kaynağı | Claim yerine host. Reddedildi (aşağıda). |
| D. Okulun kendi alan adı (custom domain) | `app.okul.com`; okul başına sertifika ve DNS. Kapsam dışı, geri dönüş koşuluna yazıldı. |

## Değerlendirme

- **Tenant tanımlama ve savunma derinliği.** C güvenlik açısından geri adımdır: host istemcinin kontrolündeki bir girdidir; tenant'ı ondan çıkarmak ADR-0003'te "HTTP başlığı (güvensiz)" diye elenen modelin aynısıdır ve imzalı claim'i bırakıp sahte edilebilir bir kaynağa güvenmek demektir. B ek bir kontrol ekler ama katmanlı savunmaya katkısı sınırlıdır: beş katmanın (filtre, yazma koruması, bileşik FK, benzersiz index, izolasyon testleri) hiçbiri host'a bağlı değildir ve asıl tehdit (başka okulun verisi) zaten `404` ve DB kısıtlarıyla kapalıdır. B'nin getirdiği yeni risk confused deputy'dir: host ile claim çelişirse hangisinin kazandığı, host'suz yollarda (arka plan işleri, webhook, e-posta bağlantıları) tenant'ın nereden geleceği ayrı kural ister; her kural yeni bir yanlış yapılandırma yüzeyidir. Webhook zaten host'tan değil token'dan çözülüyor (DEC-344); host'a dayalı model bu yolda uygulanamaz ve iki çözümleme modeli birlikte yaşatılmak zorunda kalır.
- **Wildcard TLS ve DNS.** `*.alanadi` sertifikası Caddy'de DNS-01 doğrulaması ister; bu, Cloudflare API anahtarını sunucuya koymak demektir. Planlanan "DNS only" yapı (DEC-087, P0-30) ve en az yetki ilkesiyle çelişir; sunucu da henüz alınmadı (Deploy bloğu). Okul eklemek DNS'siz olur, ama sertifika ve Caddy yapılandırması bir güvenlik bileşenine dönüşür. Custom domain'de (D) her okul için ayrı sertifika, doğrulama ve destek yükü vardır.
- **Çerez kapsamı, CSRF ve SameSite.** Çerez host-only ve `SameSite=Strict`; her alt alan adı kendi çerezini alır, okullar arası sızıntı olmaz. `Domain=.alanadi` verilirse tüm okullar çerezi paylaşır ve bir alt alan adındaki XSS hepsine uzanır; bu asla yapılmamalıdır. Ayrıca `*.alanadi` tek site sayılır: alt alan adları arası istekler `SameSite=Strict` açısından same-site'tır. Bir okulun alt alan adı ele geçirilirse (ör. unutulmuş DNS kaydı) diğer okulun API'sine same-site istek atılabilir; bugünkü tek origin bunu yaşatmaz. Public Suffix List'e girmeden bu zayıflık kalır. Yanlış alt alan adından giren kullanıcı için "doğru adrese yönlendir" davranışı da yazılmalıdır.
- **CORS.** SPA'lar API'yi kendi host'larındaki `/api` ile çağırırsa CORS kapalı kalır, ama her alt alan adının Caddy'de API'ye de yönlenmesi gerekir. Aksi halde CORS ve çerez için `credentials` açılır; bu, ADR-0006'daki "aynı origin, CORS yok" ilkesinden vazgeçmektir.
- **Okul müdürü açısından marka değeri.** Veli adresi hiç yazmaz, bağlantıyı e-postadan veya mesajdan tıklar; alt alan adının görünür değeri küçüktür. Güven ve tanınırlık için işe yarayan okul adı ve logosunun arayüzde görünmesidir; bu alt alan adı olmadan yapılabilir. Gerçek white-label (kendi logosu, kendi alan adı) ancak okul sayısı ve sözleşme talebi oluşunca anlamlıdır; bugün böyle bir talep yok.
- **SEO.** Uygulamalar girişli ve indekslenmeyecek; arama görünürlüğü ilgisiz. Okulun herkese açık tanıtım sayfası ayrı bir üründür, kapsamda yok.
- **Ödeme ve e-posta bağlantıları.** Tek `CallbackUrl` ve tek `AppBaseUrl` bugün sabittir ve sağlayıcıya bir kez kaydedilir. Alt alan adında bağlantılar okul slug'ından üretilmeli (`Slug` platform genelinde benzersiz, mümkün), sağlayıcıda dönüş adresleri okul başına tanımlanmalı veya hepsi merkezî adreste kalıp sonra yönlendirilmelidir; ikincisi alt alan adının amacını boşa çıkarır. Slug değişikliği eski e-posta bağlantılarını ve sağlayıcı kayıtlarını kırar.
- **Yerel geliştirme ve test.** `*.localhost` çalışır, ama e2e (admin 4300), Vite proxy'leri ve iki uygulama için çoklu host yapılandırması, çerez ve host-başlığı testleri gerekir; her host kuralı test ve CI maliyetidir.
- **Maliyet.** Domain ve wildcard sertifika neredeyse bedava, bütçe 20 USD/ay; asıl maliyet kod, test, güvenlik incelemesi ve işletme (DNS-01 anahtarı, yönlendirme kuralları) emeğidir. Karşılığında bugün ölçülebilir bir talep yok.
- **KVKK.** Etkisiz: alt alan adı kişisel veri taşımaz, veri yerleşimi değişmez. Slug'a öğrenci veya veli bilgisi konmaz.

## Karar

**Şimdi yapılmaz.** Tek alan adı (`app.` / `staging.`, DEC-087), tenant `tenant_id` claim'inden; çerez, CORS, `Email:AppBaseUrl` ve ödeme `CallbackUrl` olduğu gibi kalır. ADR-0003'teki "alt alan adı" seçeneği bu ADR ile ertelenmiş olarak kapanır. C hiçbir koşulda seçilmez: tenant'ın tek kaynağı imzalı claim'dir.

## Geri dönüş koşulları

Şunlardan biri oluşursa yeniden değerlendirilir:

1. Bir okul (veya yeni müşteri) yazılı olarak kendi adresini veya markasını (white-label) istiyor ve buna ödemeye hazır; bu durumda D (custom domain) ve B birlikte düşünülür.
2. Okul sayısı, okul bazında hız sınırı, kötüye kullanım engelleme veya destek ayrımı gerektirecek kadar büyüdü.
3. Sunucu ve Caddy alındıktan sonra wildcard sertifikanın DNS-01 anahtarı güvenle yönetilebiliyor.

Dönüldüğünde önkoşullar: (a) host yalnızca ek doğrulamadır, claim'in yerine geçmez, uyuşmazlık `404`; (b) çerez host-only kalır, `Domain` asla verilmez; (c) her alt alan adı API'ye aynı origin ile yönlenir, CORS kapalı kalır; (d) bağlantılar (e-posta, ödeme dönüşü) okul slug'ından üretilir ve slug değişikliği yönetilir; (e) unutulmuş DNS kayıtları (subdomain takeover) için envanter, ayrı kayıtlı alan adı veya Public Suffix List değerlendirilir.

## Sonuçlar

- Kod, altyapı ve test değişikliği yok; çalışma doküman güncellemesidir.
- Okul markası ihtiyacı o zamana kadar okul adı ve logosunun arayüzde gösterilmesiyle karşılanabilir; bu ayrı bir roadmap maddesi olarak eklenebilir (sahip kararı).
- Sonradan alt alan adına geçilirse mevcut e-posta bağlantıları (`AppBaseUrl`) ve sağlayıcıdaki dönüş adresi için geçiş dönemi ve yönlendirme gerekir; bugün tek adres olduğu için bu bedel küçüktür, erteleme borç yaratmaz.
