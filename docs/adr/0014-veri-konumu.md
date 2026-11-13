# ADR-0014 — Gerçek verinin barındırma konumu (Türkiye mi, yurt dışı mı)

- **Durum:** Proposed (agent, 2026-11-13). **Karar sahipte**; gerçek veri kullanılmadan önce (Deploy bloğu) verilmeli.
- **İlgili:** NFR-PRV-07, P4-06, DEC-544, DEC-087 (tek VPS, Caddy), ADR-0011 (veritabanı rolleri), DEC-320 (S3 uyumlu
  belge deposu), [aydınlatma metni taslağı](../aydinlatma-metni.md)

## Bağlam

Spor Akademi öğrencilerin (çoğu reşit değil) kimlik, iletişim ve okul kayıtlarını, velilerin iletişim bilgilerini ve
**özel nitelikli kişisel veri** olan sağlık raporlarını işler. Bugüne kadar yalnız sentetik veri kullanıldı. Gerçek veriye
geçmeden barındırma konumu KVKK açısından belirlenmeli (NFR-PRV-07).

Verinin bulunduğu yerler: PostgreSQL (asıl veri), S3 uyumlu belge deposu (sağlık raporu, fotoğraf), yedekler, Valkey
önbelleği (kısa ömürlü okuma kopyaları), loglar ve izler (kişisel veri içermez, NFR-PRV-03), e-posta gönderim hizmeti
(ad ve e-posta adresi), online ödeme kuruluşu (ödeme için gereken bilgiler).

KVKK md. 9 (7499 sayılı Kanunla 2024'te değişen hâli ve Yurt Dışına Aktarım Yönetmeliği): yurt dışına aktarım ya
(a) Kurul'un **yeterlilik kararı** olan ülkeye, ya (b) yeterlilik kararı yoksa **uygun güvencelerden** biriyle
(Kurul'un ilan ettiği **standart sözleşme**, bağlayıcı şirket kuralları, taahhütname) ya da (c) arızi hâllerde md. 9/6
istisnalarıyla yapılabilir. Standart sözleşme imzalandıktan sonra **5 iş günü içinde Kurul'a bildirilir**. Bu ADR
yazılırken Kurul'un ilan ettiği bir yeterlilik kararı bilinmiyor; bu nedenle yurt dışı seçeneği pratikte standart
sözleşme demektir. Hukuki teyit sahiptedir.

Yurt içinde barındırmada md. 9 uygulanmaz; ama yurt dışı merkezli bir alt hizmet (e-posta, izleme, destek erişimi)
yine aktarım sayılabilir.

## Seçenekler

| Seçenek | Özet | KVKK | Maliyet ve işletme |
| --- | --- | --- | --- |
| A. Türkiye'de VPS ve nesne deposu | Veritabanı, belgeler ve yedekler Türkiye'deki bir sağlayıcıda | md. 9 yok; en basit aydınlatma | Fiyat benzer veya biraz yüksek; yönetilen hizmet (yönetilen PostgreSQL, S3) seçeneği az, SLA ve destek sağlayıcıya göre değişir |
| B. AB'de VPS (ör. Almanya), standart sözleşmeyle | Bugünkü planın (DEC-087) doğal hâli | Standart sözleşme + Kurul'a bildirim; aydınlatma metninde aktarım açıkça yazılır | En ucuz ve olgun seçenek (20 USD/ay bütçesine rahat sığar); S3 uyumlu depo ve yedek kolay |
| C. Karma: veritabanı ve belgeler Türkiye'de, yedek ve izleme yurt dışında | Asıl veri yurt içinde | Şifreli yedek de kişisel veridir; yurt dışındaki yedek için yine md. 9 | İki sağlayıcı, iki fatura, daha çok işletme yükü |

## Değerlendirme

- **Hukuki risk ve iş yükü.** A, md. 9 sürecini tamamen dışarıda bırakır; okul müdürüne ve veliye anlatması en kolay
  seçenektir ("verileriniz Türkiye'de"). B'de her okul (veri sorumlusu) için standart sözleşme zinciri ve bildirim gerekir;
  sözleşme yükü platform sahibinde toplanabilir ama sorumluluk okulda kalır. Sağlık verisi ve çocuk verisi olduğu için
  Kurul'un ve velilerin hassasiyeti yüksektir.
- **Teknik uygunluk.** Uygulama sağlayıcıdan bağımsızdır: Docker imajları, PostgreSQL, S3 uyumlu depo (SeaweedFS ile de
  çalışır), Valkey. Konum kararı kodu değiştirmez; yalnız Deploy bloğundaki sağlayıcı seçimini ve aydınlatma metninin
  "Aktarım" bölümünü değiştirir.
- **Alt hizmetler her durumda aktarım yaratabilir.** E-posta gönderimi ve hata takibi (P4-07, Sentry) yurt dışı
  hizmetse, A seçilse bile bu hizmetler için md. 9 değerlendirilmelidir. Loglar ve izler kişisel veri taşımaz
  (NFR-PRV-03, P4-07), Sentry'ye kişisel veri gönderilmez; e-posta adresleri ise gider.
- **Yedek.** Yedek şifrelenerek alınsa da kişisel veridir; yedeğin konumu asıl verinin konumuyla aynı kuralla seçilir.

## Öneri (agent)

**A: Türkiye'de barındırma**, en azından veritabanı, belge deposu ve yedekler için. Gerekçe: çocuk ve sağlık verisi
işleniyor; md. 9 sürecini (standart sözleşme, bildirim, her okulun aydınlatma metninde aktarım) baştan ortadan kaldırmak
ürünün satış ve güven tarafında da artıdır; teknik maliyeti düşüktür, çünkü uygulama sağlayıcıdan bağımsızdır. E-posta
gönderimi için yurt içi bir sağlayıcı ya da md. 9 değerlendirmesi ayrıca yapılır.

B, maliyet veya hizmet kalitesi nedeniyle tercih edilirse: standart sözleşme imzalanır ve 5 iş günü içinde Kurul'a
bildirilir, aydınlatma metninin "Aktarım" bölümü ülke ve sağlayıcı adıyla güncellenir, yedekler aynı bölgede tutulur.

## Sahibe kalan karar

1. A, B veya C (ve sağlayıcı adı).
2. E-posta gönderim sağlayıcısı ve konumu.
3. B veya C seçilirse standart sözleşme sürecini kimin yürüteceği (platform sahibi mi, her okul mu) ve hukuki teyit.

## Geri dönüş koşulu

Kurul bir ülke için yeterlilik kararı ilan ederse ya da seçilen sağlayıcı hizmet kalitesini karşılayamazsa yeniden
değerlendirilir. Konum değişikliği bir veri taşıma işidir (yedekten geri yükleme tatbikatı, P4-09), kod değişikliği değildir.
